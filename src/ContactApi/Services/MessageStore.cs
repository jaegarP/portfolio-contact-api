using Azure.Data.Tables;
using ContactApi.Core;
using ContactApi.Core.Models;
using Microsoft.Extensions.Logging;

namespace ContactApi.Services;

/// <summary>Azure Table Storage implementation: one row per message in the "ContactMessages" table.</summary>
public sealed class TableMessageStore(TableClient table, ILogger<TableMessageStore> logger) : IMessageStore
{
    private bool _tableReady;

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        if (_tableReady) return;
        await table.CreateIfNotExistsAsync(ct);
        _tableReady = true;
    }

    public async Task<int> CountRecentAsync(string ipHash, TimeSpan window, CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        var since = DateTimeOffset.UtcNow - window;
        var filter = TableClient.CreateQueryFilter($"IpHash eq {ipHash} and ReceivedAt ge {since}");
        var count = 0;
        await foreach (var _ in table.QueryAsync<TableEntity>(filter, select: ["RowKey"], cancellationToken: ct))
            count++;
        return count;
    }

    public async Task<string> SaveAsync(ContactSubmission s, string ipHash, string? userAgent, CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        var now = DateTimeOffset.UtcNow;
        // Newest first when browsing the table: RowKey sorts ascending, so invert the timestamp.
        var rowKey = $"{DateTimeOffset.MaxValue.Ticks - now.Ticks:D19}-{Guid.NewGuid():N}";
        var entity = new TableEntity(partitionKey: now.ToString("yyyy-MM"), rowKey: rowKey)
        {
            ["Name"] = s.Name,
            ["Email"] = s.Email,
            ["Message"] = s.Message,
            ["IpHash"] = ipHash,
            ["UserAgent"] = userAgent?[..Math.Min(userAgent.Length, 300)],
            ["ReceivedAt"] = now,
        };
        await table.AddEntityAsync(entity, ct);
        logger.LogInformation("Stored contact message {RowKey}", rowKey);
        return rowKey;
    }
}
