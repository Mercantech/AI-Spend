using AiSpend.Api.Models;

namespace AiSpend.Api.Dtos;

public record OrgDto(Guid Id, string Name, string Role);
public record ConnectionDto(
    Guid Id, string Provider, string Scope, string Label, bool IsActive,
    DateTimeOffset? LastSyncAt, string? LastSyncError, bool HasCredential);
public record CreateConnectionRequest(string Provider, string? Label, string ApiKey, string? Scope);
public record BackfillRequest(Guid? OrganizationId, Guid? ConnectionId, DateTimeOffset? From, DateTimeOffset? To);
public record UsageSummaryDto(
    DateTimeOffset From, DateTimeOffset To,
    decimal CostUsd, decimal ChargedUsd,
    long InputTokens, long OutputTokens, long CacheWriteTokens, long CacheReadTokens,
    long RequestCount, int EventCount);
public record SeriesPointDto(
    string Key, string Day,
    long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens,
    decimal CostUsd, long RequestCount,
    string? Provider = null);
public record UsageEventDto(
    Guid Id, string Provider, DateTimeOffset OccurredAt, string? Model, string? UserEmail, string? Kind,
    long InputTokens, long OutputTokens, long CacheWriteTokens, long CacheReadTokens,
    long RequestCount, decimal CostCents, decimal ChargedCents, string? ConversationId);
public record LiveMeterDto(string Provider, string Label, double UsedPercent, DateTimeOffset? ResetsAt, string? DisplayValue, DateTimeOffset MeasuredAt);
public record PagedEventsDto(IReadOnlyList<UsageEventDto> Items, int Total, int Page, int PageSize);

public static class DtoMaps
{
    public static string ProviderName(ProviderType p) => p switch
    {
        ProviderType.Cursor => "cursor",
        ProviderType.OpenAi => "openai",
        ProviderType.Codex => "codex",
        _ => p.ToString().ToLowerInvariant()
    };

    public static ProviderType ParseProvider(string s) => s.Trim().ToLowerInvariant() switch
    {
        "cursor" => ProviderType.Cursor,
        "openai" or "open_ai" or "oai" => ProviderType.OpenAi,
        "codex" => ProviderType.Codex,
        _ => throw new ArgumentException($"Ukendt provider: {s}")
    };

    public static ConnectionScope ParseScope(string? s) =>
        string.Equals(s, "organization", StringComparison.OrdinalIgnoreCase)
            ? ConnectionScope.Organization
            : ConnectionScope.Personal;
}
