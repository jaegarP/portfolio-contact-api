using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using ContactApi.Core;
using ContactApi.Core.Models;

namespace ContactApi.Aws;

/// <summary>
/// Amazon DynamoDB implementation.
/// Table:  partition key "Month" (yyyy-MM) + sort key "Id" (newest first)
/// Index "ByIp": partition key "IpHash" + sort key "ReceivedAt" — lets the rate limit count a
/// visitor's recent messages with one cheap query instead of scanning the table.
/// </summary>
public sealed class DynamoDbMessageStore(
    IAmazonDynamoDB db,
    string tableName,
    bool createTableIfMissing,
    ILogger<DynamoDbMessageStore> logger) : IMessageStore
{
    public const string IpIndex = "ByIp";
    private bool _ready;

    public async Task<int> CountRecentAsync(string ipHash, TimeSpan window, CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        var since = (DateTimeOffset.UtcNow - window).UtcDateTime.ToString("O");
        var count = 0;
        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            var page = await db.QueryAsync(new QueryRequest
            {
                TableName = tableName,
                IndexName = IpIndex,
                KeyConditionExpression = "IpHash = :ip AND ReceivedAt >= :since",
                ExpressionAttributeValues = new()
                {
                    [":ip"] = new AttributeValue { S = ipHash },
                    [":since"] = new AttributeValue { S = since },
                },
                Select = Select.COUNT,
                ExclusiveStartKey = startKey,
            }, ct);
            count += page.Count ?? 0;
            startKey = page.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        } while (startKey is not null);
        return count;
    }

    public async Task<string> SaveAsync(ContactSubmission s, string ipHash, string? userAgent, CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var id = $"{DateTimeOffset.MaxValue.Ticks - now.Ticks:D19}-{Guid.NewGuid():N}"; // sorts newest first
        var item = new Dictionary<string, AttributeValue>
        {
            ["Month"] = new() { S = now.ToString("yyyy-MM") },
            ["Id"] = new() { S = id },
            ["Name"] = new() { S = s.Name },
            ["Email"] = new() { S = s.Email },
            ["Message"] = new() { S = s.Message },
            ["IpHash"] = new() { S = ipHash },
            ["ReceivedAt"] = new() { S = now.UtcDateTime.ToString("O") },
        };
        if (!string.IsNullOrEmpty(userAgent))
            item["UserAgent"] = new() { S = userAgent[..Math.Min(userAgent.Length, 300)] };

        await db.PutItemAsync(new PutItemRequest { TableName = tableName, Item = item }, ct);
        logger.LogInformation("Stored contact message {Id}", id);
        return id;
    }

    /// <summary>
    /// Local development only (DynamoDB Local in Docker): create the table on first use.
    /// In AWS the table is created by the SAM template, so this is switched off there.
    /// </summary>
    private async Task EnsureTableAsync(CancellationToken ct)
    {
        if (_ready || !createTableIfMissing) { _ready = true; return; }
        try
        {
            await db.DescribeTableAsync(tableName, ct);
        }
        catch (ResourceNotFoundException)
        {
            logger.LogInformation("Creating DynamoDB table {Table}", tableName);
            await db.CreateTableAsync(new CreateTableRequest
            {
                TableName = tableName,
                BillingMode = BillingMode.PAY_PER_REQUEST,
                AttributeDefinitions =
                [
                    new("Month", ScalarAttributeType.S), new("Id", ScalarAttributeType.S),
                    new("IpHash", ScalarAttributeType.S), new("ReceivedAt", ScalarAttributeType.S),
                ],
                KeySchema = [new("Month", KeyType.HASH), new("Id", KeyType.RANGE)],
                GlobalSecondaryIndexes =
                [
                    new GlobalSecondaryIndex
                    {
                        IndexName = IpIndex,
                        KeySchema = [new("IpHash", KeyType.HASH), new("ReceivedAt", KeyType.RANGE)],
                        Projection = new Projection { ProjectionType = ProjectionType.KEYS_ONLY },
                    },
                ],
            }, ct);
        }
        _ready = true;
    }
}
