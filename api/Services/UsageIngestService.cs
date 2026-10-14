using AiSpend.Api.Data;
using AiSpend.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AiSpend.Api.Services;

public sealed class UsageIngestService(
    AppDbContext db,
    CursorUsageClient cursor,
    OpenAiUsageClient openAi,
    CodexUsageClient codex,
    CredentialProtector protector,
    ILogger<UsageIngestService> log)
{
    public async Task<(int Upserted, int RawStored)> IngestCursorAsync(
        ProviderConnection connection,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connection.EncryptedCredential))
            throw new InvalidOperationException("Cursor-forbindelse mangler API-nøgle");

        var apiKey = protector.Decrypt(connection.EncryptedCredential);
        var events = await cursor.FetchFilteredUsageEventsAsync(apiKey, from, to, ct);
        try
        {
            var meters = await cursor.FetchLiveMetersAsync(apiKey, ct);
            if (meters.Count > 0)
                await UpsertLiveMetersAsync(connection.OrganizationId, ProviderType.Cursor, meters, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Cursor live meters kunne ikke hentes");
        }

        var upserted = 0;
        var rawStored = 0;

        foreach (var ev in events)
        {
            var existing = await db.UsageEvents.FirstOrDefaultAsync(x =>
                x.OrganizationId == connection.OrganizationId
                && x.Provider == ProviderType.Cursor
                && x.ExternalKey == ev.ExternalKey, ct);

            ProviderRaw? raw = null;
            if (existing is null)
            {
                raw = new ProviderRaw
                {
                    OrganizationId = connection.OrganizationId,
                    Provider = ProviderType.Cursor,
                    PayloadJson = ev.RawJson
                };
                db.ProviderRaws.Add(raw);
                rawStored++;
            }

            if (existing is null)
            {
                db.UsageEvents.Add(new UsageEvent
                {
                    OrganizationId = connection.OrganizationId,
                    Provider = ProviderType.Cursor,
                    ExternalKey = ev.ExternalKey,
                    OccurredAt = ev.OccurredAt,
                    Model = ev.Model,
                    UserEmail = ev.UserEmail,
                    Kind = ev.Kind,
                    ConversationId = ev.ConversationId,
                    IsTokenBasedCall = ev.IsTokenBasedCall,
                    InputTokens = ev.InputTokens,
                    OutputTokens = ev.OutputTokens,
                    CacheWriteTokens = ev.CacheWriteTokens,
                    CacheReadTokens = ev.CacheReadTokens,
                    CostCents = ev.TotalCents,
                    ChargedCents = ev.ChargedCents,
                    CursorTokenFee = ev.CursorTokenFee,
                    RequestsCosts = ev.RequestsCosts,
                    RequestCount = 1,
                    RawPayload = raw
                });
                upserted++;
            }
            else
            {
                existing.OccurredAt = ev.OccurredAt;
                existing.Model = ev.Model;
                existing.UserEmail = ev.UserEmail;
                existing.Kind = ev.Kind;
                existing.ConversationId = ev.ConversationId;
                existing.IsTokenBasedCall = ev.IsTokenBasedCall;
                existing.InputTokens = ev.InputTokens;
                existing.OutputTokens = ev.OutputTokens;
                existing.CacheWriteTokens = ev.CacheWriteTokens;
                existing.CacheReadTokens = ev.CacheReadTokens;
                existing.CostCents = ev.TotalCents;
                existing.ChargedCents = ev.ChargedCents;
                existing.CursorTokenFee = ev.CursorTokenFee;
                existing.RequestsCosts = ev.RequestsCosts;
                existing.IngestedAt = DateTimeOffset.UtcNow;
                upserted++;
            }
        }

        await db.SaveChangesAsync(ct);
        await RebuildDailyAsync(connection.OrganizationId, ProviderType.Cursor, from, to, ct);

        connection.LastSyncAt = DateTimeOffset.UtcNow;
        connection.LastSyncError = null;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Cursor ingest org={Org} upserted={N} raw={R}", connection.OrganizationId, upserted, rawStored);
        return (upserted, rawStored);
    }

    public async Task<(int Upserted, int CostRows)> IngestOpenAiAsync(
        ProviderConnection connection,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connection.EncryptedCredential))
            throw new InvalidOperationException("OpenAI-forbindelse mangler Admin-nøgle");

        var key = protector.Decrypt(connection.EncryptedCredential);
        var usage = await openAi.FetchCompletionsUsageAsync(key, from, to, ct);
        var upserted = 0;

        foreach (var u in usage)
        {
            var existing = await db.UsageEvents.FirstOrDefaultAsync(x =>
                x.OrganizationId == connection.OrganizationId
                && x.Provider == ProviderType.OpenAi
                && x.ExternalKey == u.ExternalKey, ct);

            if (existing is null)
            {
                var raw = new ProviderRaw
                {
                    OrganizationId = connection.OrganizationId,
                    Provider = ProviderType.OpenAi,
                    PayloadJson = u.RawJson
                };
                db.ProviderRaws.Add(raw);
                db.UsageEvents.Add(new UsageEvent
                {
                    OrganizationId = connection.OrganizationId,
                    Provider = ProviderType.OpenAi,
                    ExternalKey = u.ExternalKey,
                    OccurredAt = u.OccurredAt,
                    Model = u.Model,
                    Kind = "completions",
                    InputTokens = u.InputTokens,
                    OutputTokens = u.OutputTokens,
                    CacheReadTokens = u.CacheReadTokens,
                    RequestCount = u.RequestCount,
                    RawPayload = raw
                });
                upserted++;
            }
            else
            {
                existing.InputTokens = u.InputTokens;
                existing.OutputTokens = u.OutputTokens;
                existing.CacheReadTokens = u.CacheReadTokens;
                existing.RequestCount = u.RequestCount;
                existing.Model = u.Model;
                existing.IngestedAt = DateTimeOffset.UtcNow;
                upserted++;
            }
        }

        var monthStart = new DateTimeOffset(from.Year, from.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var costs = await openAi.FetchCostsAsync(key, monthStart, ct);
        foreach (var c in costs)
        {
            var day = DateOnly.FromDateTime(c.DayStart.UtcDateTime);
            var row = await db.UsageDaily.FirstOrDefaultAsync(x =>
                x.OrganizationId == connection.OrganizationId
                && x.Day == day
                && x.Provider == ProviderType.OpenAi
                && x.Model == "__cost__"
                && x.UserEmail == "", ct);

            if (row is null)
            {
                db.UsageDaily.Add(new UsageDaily
                {
                    OrganizationId = connection.OrganizationId,
                    Day = day,
                    Provider = ProviderType.OpenAi,
                    Model = "__cost__",
                    CostCents = c.AmountUsd * 100m
                });
            }
            else
            {
                row.CostCents = c.AmountUsd * 100m;
            }
        }

        await db.SaveChangesAsync(ct);
        await RebuildDailyAsync(connection.OrganizationId, ProviderType.OpenAi, from, to, ct);

        // Apply cost onto daily totals without wiping token rows
        foreach (var c in costs)
        {
            var day = DateOnly.FromDateTime(c.DayStart.UtcDateTime);
            var tokenRows = await db.UsageDaily
                .Where(x => x.OrganizationId == connection.OrganizationId
                            && x.Day == day
                            && x.Provider == ProviderType.OpenAi
                            && x.Model != "__cost__")
                .ToListAsync(ct);
            // Keep __cost__ as dedicated reconciliation row; also stamp live meter
            await UpsertLiveMeterAsync(connection.OrganizationId, ProviderType.OpenAi, "Måned",
                0, null, $"${costs.Sum(x => x.AmountUsd):0.##}", ct);
        }

        connection.LastSyncAt = DateTimeOffset.UtcNow;
        connection.LastSyncError = null;
        await db.SaveChangesAsync(ct);
        return (upserted, costs.Count);
    }

    public async Task<(int Upserted, int MeterCount, long? LifetimeTokens)> IngestCodexAsync(
        ProviderConnection connection,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connection.EncryptedCredential))
            throw new InvalidOperationException("Codex-forbindelse mangler ChatGPT-session-cookie");

        var cookie = protector.Decrypt(connection.EncryptedCredential);
        var snap = await codex.FetchAsync(cookie, ct);

        if (snap.Meters.Count > 0)
            await UpsertLiveMetersAsync(connection.OrganizationId, ProviderType.Codex, snap.Meters, ct);

        if (!string.IsNullOrWhiteSpace(snap.RawUsageJson))
        {
            db.ProviderRaws.Add(new ProviderRaw
            {
                OrganizationId = connection.OrganizationId,
                Provider = ProviderType.Codex,
                PayloadJson = snap.RawUsageJson
            });
        }
        if (!string.IsNullOrWhiteSpace(snap.RawProfileJson))
        {
            db.ProviderRaws.Add(new ProviderRaw
            {
                OrganizationId = connection.OrganizationId,
                Provider = ProviderType.Codex,
                PayloadJson = snap.RawProfileJson
            });
        }

        var upserted = 0;
        DateTimeOffset? minDay = null;
        DateTimeOffset? maxDay = null;

        foreach (var b in snap.DailyBuckets)
        {
            var occurred = new DateTimeOffset(b.Day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            minDay = minDay is null || occurred < minDay ? occurred : minDay;
            maxDay = maxDay is null || occurred > maxDay ? occurred : maxDay;

            var existing = await db.UsageEvents.FirstOrDefaultAsync(x =>
                x.OrganizationId == connection.OrganizationId
                && x.Provider == ProviderType.Codex
                && x.ExternalKey == b.ExternalKey, ct);

            if (existing is null)
            {
                db.UsageEvents.Add(new UsageEvent
                {
                    OrganizationId = connection.OrganizationId,
                    Provider = ProviderType.Codex,
                    ExternalKey = b.ExternalKey,
                    OccurredAt = occurred,
                    Model = "codex",
                    Kind = "profile_daily",
                    // ChatGPT profile buckets are total tokens (ikke input/output-split).
                    InputTokens = b.Tokens,
                    OutputTokens = 0,
                    RequestCount = 1
                });
                upserted++;
            }
            else
            {
                existing.InputTokens = b.Tokens;
                existing.OccurredAt = occurred;
                existing.IngestedAt = DateTimeOffset.UtcNow;
                upserted++;
            }
        }

        if (snap.LifetimeTokens is long life)
        {
            await UpsertLiveMeterAsync(connection.OrganizationId, ProviderType.Codex, "Lifetime tokens",
                0, null, life.ToString("N0"), ct);
        }

        if (!string.IsNullOrWhiteSpace(snap.PlanType))
        {
            await UpsertLiveMeterAsync(connection.OrganizationId, ProviderType.Codex, "Plan",
                0, null, snap.PlanType, ct);
        }

        await db.SaveChangesAsync(ct);

        if (minDay is not null && maxDay is not null)
            await RebuildDailyAsync(connection.OrganizationId, ProviderType.Codex, minDay.Value, maxDay.Value.AddDays(1), ct);

        connection.LastSyncAt = DateTimeOffset.UtcNow;
        connection.LastSyncError = null;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Codex ingest org={Org} upserted={N} meters={M}",
            connection.OrganizationId, upserted, snap.Meters.Count);
        return (upserted, snap.Meters.Count, snap.LifetimeTokens);
    }

    public async Task UpsertLiveMetersAsync(
        Guid orgId,
        ProviderType provider,
        IEnumerable<CursorLiveMeterDto> meters,
        CancellationToken ct = default)
    {
        foreach (var m in meters)
            await UpsertLiveMeterAsync(orgId, provider, m.Label, m.UsedPercent, m.ResetsAt, m.DisplayValue, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task UpsertLiveMeterAsync(
        Guid orgId, ProviderType provider, string label, double usedPercent,
        DateTimeOffset? resetsAt, string? display, CancellationToken ct)
    {
        var row = await db.LiveMeters.FirstOrDefaultAsync(x =>
            x.OrganizationId == orgId && x.Provider == provider && x.Label == label, ct);
        if (row is null)
        {
            db.LiveMeters.Add(new LiveMeter
            {
                OrganizationId = orgId,
                Provider = provider,
                Label = label,
                UsedPercent = usedPercent,
                ResetsAt = resetsAt,
                DisplayValue = display,
                MeasuredAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            row.UsedPercent = usedPercent;
            row.ResetsAt = resetsAt;
            row.DisplayValue = display;
            row.MeasuredAt = DateTimeOffset.UtcNow;
        }
    }

    public async Task RebuildDailyAsync(
        Guid orgId, ProviderType provider, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var fromDay = DateOnly.FromDateTime(from.UtcDateTime);
        var toDay = DateOnly.FromDateTime(to.UtcDateTime);

        var existing = await db.UsageDaily
            .Where(x => x.OrganizationId == orgId
                        && x.Provider == provider
                        && x.Day >= fromDay
                        && x.Day <= toDay
                        && x.Model != "__cost__")
            .ToListAsync(ct);
        db.UsageDaily.RemoveRange(existing);

        var grouped = await db.UsageEvents
            .Where(x => x.OrganizationId == orgId
                        && x.Provider == provider
                        && x.OccurredAt >= from
                        && x.OccurredAt <= to)
            .GroupBy(x => new
            {
                Day = DateOnly.FromDateTime(x.OccurredAt.UtcDateTime),
                Model = x.Model ?? "",
                UserEmail = x.UserEmail ?? ""
            })
            .Select(g => new UsageDaily
            {
                OrganizationId = orgId,
                Day = g.Key.Day,
                Provider = provider,
                Model = g.Key.Model,
                UserEmail = g.Key.UserEmail,
                InputTokens = g.Sum(x => x.InputTokens),
                OutputTokens = g.Sum(x => x.OutputTokens),
                CacheWriteTokens = g.Sum(x => x.CacheWriteTokens),
                CacheReadTokens = g.Sum(x => x.CacheReadTokens),
                RequestCount = g.Sum(x => x.RequestCount),
                CostCents = g.Sum(x => x.CostCents),
                ChargedCents = g.Sum(x => x.ChargedCents)
            })
            .ToListAsync(ct);

        db.UsageDaily.AddRange(grouped);
        await db.SaveChangesAsync(ct);
    }
}
