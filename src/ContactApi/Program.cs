// ---------------------------------------------------------------
// Azure host: wires the shared ContactService to Azure services.
//   Database → Azure Table Storage     Email → Azure Communication Services
// ---------------------------------------------------------------
using Azure.Communication.Email;
using Azure.Data.Tables;
using ContactApi.Core;
using ContactApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

var config = builder.Configuration;

// ---- Shared logic ----
builder.Services.AddSingleton(ContactOptions.From(key => config[key]));
builder.Services.AddSingleton<ContactService>();

// ---- Message storage (Azure Table Storage). Falls back to a no-op store when not configured. ----
var storageConnection = config["MessagesStorageConnectionString"];
if (!string.IsNullOrWhiteSpace(storageConnection))
{
    builder.Services.AddSingleton(new TableClient(storageConnection, config["MessagesTableName"] ?? "ContactMessages"));
    builder.Services.AddSingleton<IMessageStore, TableMessageStore>();
}
else
{
    builder.Services.AddSingleton<IMessageStore, NullMessageStore>();
}

// ---- Email (Azure Communication Services). Falls back to logging when not configured. ----
var acsConnection = config["CommunicationServicesConnectionString"];
var sender = config["SenderEmail"];
var recipient = config["NotifyEmail"];
if (!string.IsNullOrWhiteSpace(acsConnection) && !string.IsNullOrWhiteSpace(sender) && !string.IsNullOrWhiteSpace(recipient))
{
    builder.Services.AddSingleton<IEmailNotifier>(sp => new AcsEmailNotifier(
        new EmailClient(acsConnection), sender, recipient, sp.GetRequiredService<ILogger<AcsEmailNotifier>>()));
}
else
{
    builder.Services.AddSingleton<IEmailNotifier, LogOnlyEmailNotifier>();
}

builder.Build().Run();
