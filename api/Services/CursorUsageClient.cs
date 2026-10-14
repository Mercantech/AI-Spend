using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiSpend.Api.Services;

public sealed record CursorUsageEventDto(
    string ExternalKey,
    DateTimeOffset OccurredAt,
    string? Model,
    string? UserEmail,
    string? Kind,
    string? ConversationId,
    bool? IsTokenBasedCall,
    long InputTokens,
    long OutputTokens,
    long CacheWriteTokens,
    long CacheReadTokens,
    decimal TotalCents,
    decimal ChargedCents,
    decimal CursorTokenFee,
    decimal RequestsCosts,
    string RawJson);

public sealed record CursorLiveMeterDto(
    string Label,
    double UsedPercent,
    DateTimeOffset? ResetsAt,
    string? DisplayValue);

public sealed class CursorUsageClient(HttpClient http, ILogger<CursorUsageClient> log)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<CursorUsageEventDto>> FetchFilteredUsageEventsAsync(
        string apiKey,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        // Official Admin API (Enterprise). Falls back to dashboard twin path shape.
        var events = new List<CursorUsageEventDto>();
        var page = 1;
        const int pageSize = 1000;

        while (true)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.cursor.com/teams/filtered-usage-events");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var body = new
            {
                startDate = from.ToUnixTimeMilliseconds(),
                endDate = to.ToUnixTimeMilliseconds(),
                page,
                pageSize
            };
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                // Try org endpoint
                if (page == 1)
                {
                    var orgEvents = await FetchOrgEventsAsync(apiKey, from, to, ct);
                    if (orgEvents is not null) return orgEvents;
                }
                log.LogWarning("Cursor filtered-usage-events failed {Status}: {Body}", (int)res.StatusCode, Truncate(text));
                throw new InvalidOperationException($"Cursor API {(int)res.StatusCode}: {Truncate(text)}");
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var batch = ParseEventArray(root);
            events.AddRange(batch);

            var totalPages = root.TryGetProperty("totalPages", out var tp) ? tp.GetInt32()
                : root.TryGetProperty("pagination", out var pag) && pag.TryGetProperty("numPages", out var np) ? np.GetInt32()
                : batch.Count < pageSize ? page : page + 1;

            if (batch.Count == 0 || page >= totalPages) break;
            page++;
        }

        return events;
    }

    private async Task<IReadOnlyList<CursorUsageEventDto>?> FetchOrgEventsAsync(
        string apiKey, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var events = new List<CursorUsageEventDto>();
        var page = 1;
        const int pageSize = 1000;
        while (true)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.cursor.com/organizations/filtered-usage-events");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var body = new
            {
                startDate = from.ToUnixTimeMilliseconds(),
                endDate = to.ToUnixTimeMilliseconds(),
                page,
                pageSize
            };
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("Cursor org filtered-usage-events failed {Status}", (int)res.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(text);
            var batch = ParseEventArray(doc.RootElement);
            events.AddRange(batch);
            if (batch.Count < pageSize) break;
            page++;
        }
        return events;
    }

    public async Task<IReadOnlyList<CursorLiveMeterDto>> FetchCurrentPeriodUsageAsync(
        string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Cursor period usage {(int)res.StatusCode}: {Truncate(text)}");
        using var doc = JsonDocument.Parse(text);
        return ParsePeriodMeters(doc.RootElement);
    }

    public static IReadOnlyList<CursorUsageEventDto> ParseEventArray(JsonElement root)
    {
        JsonElement arr;
        if (root.ValueKind == JsonValueKind.Array) arr = root;
        else if (root.TryGetProperty("usageEventsDisplay", out var ued)) arr = ued;
        else if (root.TryGetProperty("usageEvents", out var ue)) arr = ue;
        else if (root.TryGetProperty("events", out var ev)) arr = ev;
        else if (root.TryGetProperty("data", out var data)) arr = data;
        else return [];

        var list = new List<CursorUsageEventDto>();
        foreach (var item in arr.EnumerateArray())
            list.Add(ParseOneEvent(item));
        return list;
    }

    public static CursorUsageEventDto ParseOneEvent(JsonElement item)
    {
        var raw = item.GetRawText();
        var ts = ReadTimestamp(item);
        var model = GetString(item, "model") ?? GetString(item, "modelName");
        var email = GetString(item, "userEmail") ?? GetString(item, "email");
        var kind = GetString(item, "kind") ?? GetString(item, "type");
        var conversationId = GetString(item, "conversationId") ?? GetString(item, "chatId");
        bool? isTokenBased = null;
        if (item.TryGetProperty("isTokenBasedCall", out var itb))
            isTokenBased = itb.ValueKind is JsonValueKind.True or JsonValueKind.False ? itb.GetBoolean() : null;

        long input = 0, output = 0, cacheWrite = 0, cacheRead = 0;
        if (item.TryGetProperty("tokenUsage", out var tu))
        {
            input = ReadLong(tu, "inputTokens", "input_tokens");
            output = ReadLong(tu, "outputTokens", "output_tokens");
            cacheWrite = ReadLong(tu, "cacheWriteTokens", "cache_write_tokens");
            cacheRead = ReadLong(tu, "cacheReadTokens", "cache_read_tokens");
        }
        else
        {
            input = ReadLong(item, "inputTokens", "input_tokens");
            output = ReadLong(item, "outputTokens", "output_tokens");
            cacheWrite = ReadLong(item, "cacheWriteTokens", "cache_write_tokens");
            cacheRead = ReadLong(item, "cacheReadTokens", "cache_read_tokens");
        }

        var totalCents = ReadDecimal(item, "totalCents", "total_cents");
        if (totalCents == 0 && item.TryGetProperty("tokenUsage", out var tu2))
            totalCents = ReadDecimal(tu2, "totalCents", "total_cents");

        var charged = ReadDecimal(item, "chargedCents", "charged_cents");
        var fee = ReadDecimal(item, "cursorTokenFee", "cursor_token_fee");
        var reqCost = ReadDecimal(item, "requestsCosts", "requestCosts", "requests_costs");

        var externalId = GetString(item, "id")
            ?? GetString(item, "eventId")
            ?? HashKey($"{ts:o}|{model}|{email}|{input}|{output}|{charged}|{conversationId}");

        return new CursorUsageEventDto(
            externalId, ts, model, email, kind, conversationId, isTokenBased,
            input, output, cacheWrite, cacheRead,
            totalCents, charged, fee, reqCost, raw);
    }

    public static IReadOnlyList<CursorLiveMeterDto> ParsePeriodMeters(JsonElement root)
    {
        var meters = new List<CursorLiveMeterDto>();
        if (!root.TryGetProperty("planUsage", out var plan)) return meters;

        DateTimeOffset? reset = null;
        if (root.TryGetProperty("billingCycleEnd", out var bce))
            reset = ReadUnixFlexible(bce);

        if (plan.TryGetProperty("autoPercentUsed", out var auto))
            meters.Add(new("Cursor-modeller", auto.GetDouble(), reset, null));
        if (plan.TryGetProperty("apiPercentUsed", out var api))
            meters.Add(new("Andre modeller", api.GetDouble(), reset, null));
        if (meters.Count == 0 && plan.TryGetProperty("totalPercentUsed", out var total))
            meters.Add(new("Måned", total.GetDouble(), reset, null));

        if (root.TryGetProperty("spendLimitUsage", out var spend))
        {
            var individualLimit = ReadDecimal(spend, "individualLimit");
            var individualUsed = ReadDecimal(spend, "individualUsed", "totalSpend");
            var pooledLimit = ReadDecimal(spend, "pooledLimit");
            var pooledUsed = ReadDecimal(spend, "pooledUsed", "totalSpend");
            var totalSpend = ReadDecimal(spend, "totalSpend");

            if (individualLimit > 0)
            {
                var pct = (double)(individualUsed / individualLimit * 100);
                meters.Add(new("Spend", pct, reset, $"${individualUsed / 100:0.##} / ${individualLimit / 100:0.##}"));
            }
            else if (pooledLimit > 0)
            {
                var pct = (double)(pooledUsed / pooledLimit * 100);
                meters.Add(new("Spend", pct, reset, $"${pooledUsed / 100:0.##} / ${pooledLimit / 100:0.##}"));
            }
            else if (totalSpend > 0)
            {
                meters.Add(new("Spend", 0, reset, $"${totalSpend / 100:0.##}"));
            }
        }

        return meters;
    }

    private static DateTimeOffset ReadTimestamp(JsonElement item)
    {
        if (item.TryGetProperty("timestamp", out var ts)) return ReadUnixFlexible(ts);
        if (item.TryGetProperty("time", out var t)) return ReadUnixFlexible(t);
        if (item.TryGetProperty("createdAt", out var c)) return ReadUnixFlexible(c);
        return DateTimeOffset.UtcNow;
    }

    private static DateTimeOffset ReadUnixFlexible(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number)
        {
            var n = el.GetInt64();
            return n > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(n)
                : DateTimeOffset.FromUnixTimeSeconds(n);
        }
        var s = el.GetString();
        if (long.TryParse(s, out var ln))
            return ln > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(ln)
                : DateTimeOffset.FromUnixTimeSeconds(ln);
        if (DateTimeOffset.TryParse(s, out var dto)) return dto.ToUniversalTime();
        return DateTimeOffset.UtcNow;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long ReadLong(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var p)) continue;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v)) return v;
            if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var s)) return s;
        }
        return 0;
    }

    private static decimal ReadDecimal(JsonElement el, params string[] names)
    {
        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var p)) continue;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var v)) return v;
            if (p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), out var s)) return s;
        }
        return 0;
    }

    private static string HashKey(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..40].ToLowerInvariant();
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
