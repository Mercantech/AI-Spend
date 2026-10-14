using AiSpend.Api.Data;
using AiSpend.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AiSpend.Api.Services;

public sealed class IngestBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<IngestBackgroundService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncAllAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Periodisk ingest fejlede");
            }

            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

    private async Task SyncAllAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ingest = scope.ServiceProvider.GetRequiredService<UsageIngestService>();

        var connections = await db.ProviderConnections
            .Where(c => c.IsActive && c.EncryptedCredential != null)
            .ToListAsync(ct);

        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-14);

        foreach (var c in connections)
        {
            try
            {
                if (c.Provider == ProviderType.Cursor)
                    await ingest.IngestCursorAsync(c, from, to, ct);
                else if (c.Provider == ProviderType.OpenAi)
                    await ingest.IngestOpenAiAsync(c, from, to, ct);
            }
            catch (Exception ex)
            {
                c.LastSyncError = ex.Message.Length > 1900 ? ex.Message[..1900] : ex.Message;
                c.LastSyncAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                log.LogWarning(ex, "Sync fejlede for connection {Id}", c.Id);
            }
        }
    }
}
