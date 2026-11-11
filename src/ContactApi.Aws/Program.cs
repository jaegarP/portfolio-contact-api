// ---------------------------------------------------------------
// AWS host: wires the shared ContactService to AWS services.
//   Database → Amazon DynamoDB        Email → Amazon SES
// Runs as a normal web server locally / in Docker; inside AWS Lambda,
// AddAWSLambdaHosting switches it to Lambda mode automatically.
// ---------------------------------------------------------------
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.SimpleEmailV2;
using ContactApi.Aws;
using ContactApi.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi); // Function URLs use the HTTP API (v2) format

var config = builder.Configuration;
var options = ContactOptions.From(key => config[key]);
var enableCors = config.GetValue("EnableCors", false);

// ---- Shared logic ----
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ContactService>();

// ---- Message storage (DynamoDB). Falls back to a no-op store when not configured. ----
var tableName = config["DynamoDbTableName"];
if (!string.IsNullOrWhiteSpace(tableName))
{
    var serviceUrl = config["DynamoDbServiceUrl"]; // set only for DynamoDB Local (Docker)
    builder.Services.AddSingleton<IAmazonDynamoDB>(_ => string.IsNullOrWhiteSpace(serviceUrl)
        ? new AmazonDynamoDBClient()                                           // AWS: credentials from the Lambda role
        : new AmazonDynamoDBClient(new BasicAWSCredentials("local", "local"),  // local: any credentials work
            new AmazonDynamoDBConfig { ServiceURL = serviceUrl, AuthenticationRegion = "us-east-1" }));
    builder.Services.AddSingleton<IMessageStore>(sp => new DynamoDbMessageStore(
        sp.GetRequiredService<IAmazonDynamoDB>(), tableName,
        createTableIfMissing: config.GetValue("DynamoDbCreateTable", false),
        sp.GetRequiredService<ILogger<DynamoDbMessageStore>>()));
}
else
{
    builder.Services.AddSingleton<IMessageStore, NullMessageStore>();
}

// ---- Email (Amazon SES). Falls back to logging when not configured. ----
var sender = config["SenderEmail"];
var recipient = config["NotifyEmail"];
if (!string.IsNullOrWhiteSpace(sender) && !string.IsNullOrWhiteSpace(recipient))
{
    builder.Services.AddSingleton<IAmazonSimpleEmailServiceV2>(_ => new AmazonSimpleEmailServiceV2Client());
    builder.Services.AddSingleton<IEmailNotifier>(sp => new SesEmailNotifier(
        sp.GetRequiredService<IAmazonSimpleEmailServiceV2>(), sender, recipient,
        sp.GetRequiredService<ILogger<SesEmailNotifier>>()));
}
else
{
    builder.Services.AddSingleton<IEmailNotifier, LogOnlyEmailNotifier>();
}

// ---- CORS: on AWS the Function URL handles it. Locally / in Docker the app does it itself. ----
if (enableCors)
{
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
        .WithOrigins([.. options.AllowedOrigins])
        .WithMethods("POST")
        .WithHeaders("Content-Type")
        .SetPreflightMaxAge(TimeSpan.FromHours(1))));
}

var app = builder.Build();
if (enableCors) app.UseCors();

// POST /api/contact — same contract as the Azure version, so the website can use either.
app.MapPost("/api/contact", async (HttpContext ctx, ContactService service, CancellationToken ct) =>
{
    var req = ctx.Request;
    var result = await service.HandleAsync(new IncomingContact(
        Origin: req.Headers.Origin.ToString(),
        ClientIp: ContactService.ResolveClientIp(
            req.Headers["X-Forwarded-For"].ToString(), ctx.Connection.RemoteIpAddress?.ToString()),
        UserAgent: req.Headers.UserAgent.ToString(),
        ContentLength: req.ContentLength,
        Body: req.Body), ct);
    return Results.Json(result.Body, statusCode: result.StatusCode);
});

// GET /api/health — quick "is it up?" check.
app.MapGet("/api/health", () => Results.Json(new { ok = true, cloud = "aws" }));

app.Run();
