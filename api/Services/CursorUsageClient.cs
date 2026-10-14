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

public enum CursorCredentialKind
{
    TeamAdminKey,
    SessionCookie,
    UserApiKeyUnsupported
}

public sealed class CursorUsageClient(HttpClient http, ILogger<CursorUsageClient> log)
{
    public static CursorCredentialKind DetectCredentialKind(string credential)
    {
        var c = credential.Trim();
        if (LooksLikeSessionCookie(c))
            return CursorCredentialKind.SessionCookie;
        // User/team API keys often share crsr_ prefix — Admin call decides which works.
        return CursorCredentialKind.TeamAdminKey;
    }

    public static bool LooksLikeSessionCookie(string credential)
    {
        var c = credential.Trim();
        if (c.StartsWith("WorkosCursorSessionToken=", StringComparison.OrdinalIgnoreCase))
            return true;
        if (c.Contains("::", StringComparison.Ordinal) || c.Contains("%3A%3A", StringComparison.OrdinalIgnoreCase))
            return true;
        // Bare JWT-like session values (not crsr_ API keys)
        if (!c.StartsWith("crsr_", StringComparison.OrdinalIgnoreCase)
            && c.Count(ch => ch == '.') >= 2
            && c.Length > 80)
            return true;
        return false;
    }

    /// <summary>Cursor capped at 30 days per request — we chunk automatically.</summary>
    public const int MaxRangeDays = 30;

    public async Task<IReadOnlyList<CursorUsageEventDto>> FetchFilteredUsageEventsAsync(
        string credential,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        if (to < from) (from, to) = (to, from);
        var all = new List<CursorUsageEventDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (windowFrom, windowTo) in ChunkDateRange(from, to, MaxRangeDays))
        {
            IReadOnlyList<CursorUsageEventDto> batch;
            if (LooksLikeSessionCookie(credential))
                batch = await FetchDashboardEventsAsync(credential, windowFrom, windowTo, ct);
            else
            {
                try
                {
                    batch = await FetchAdminEventsAsync(credential, windowFrom, windowTo, ct);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("Invalid Team API Key", StringComparison.OrdinalIgnoreCase)
                                                           || ex.Message.Contains("User API-nøgle", StringComparison.OrdinalIgnoreCase)
                                                           || ex.Message.Contains("Team API Key", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Cursor User API-nøgler kan ikke hente usage (kun Cloud Agents). " +
                        "Til personligt forbrug: indsæt WorkosCursorSessionToken fra " +
                        "https://cursor.com/dashboard/usage → DevTools (F12) → Application → Cookies. " +
                        "Til team: brug en Team Admin API-nøgle (Enterprise).");
                }
            }

            foreach (var ev in batch)
            {
                if (seen.Add(ev.ExternalKey))
                    all.Add(ev);
            }

            log.LogInformation("Cursor window {From:u}→{To:u}: {Count} events (total {Total})",
                windowFrom, windowTo, batch.Count, all.Count);
        }

        return all;
    }

    public static IEnumerable<(DateTimeOffset From, DateTimeOffset To)> ChunkDateRange(
        DateTimeOffset from, DateTimeOffset to, int maxDays)
    {
        var cursor = from;
        while (cursor < to)
        {
            var end = cursor.AddDays(maxDays);
            if (end > to) end = to;
            // Inclusive API bounds: end each window 1ms before next start when not final
            var windowTo = end < to ? end.AddMilliseconds(-1) : to;
            yield return (cursor, windowTo);
            cursor = end;
        }
    }

    public async Task ValidateCredentialAsync(string credential, CancellationToken ct = default)
    {
        var to = DateTimeOffset.UtcNow;
        var from = to.AddHours(-6);
        _ = await FetchFilteredUsageEventsAsync(credential, from, to, ct);
    }

    public async Task<IReadOnlyList<CursorLiveMeterDto>> FetchLiveMetersAsync(
        string credential, CancellationToken ct = default)
    {
        if (LooksLikeSessionCookie(credential))
            return await FetchDashboardPeriodMetersAsync(credential, ct);
        return [];
    }

    private async Task<IReadOnlyList<CursorUsageEventDto>> FetchAdminEventsAsync(
        string apiKey, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var events = new List<CursorUsageEventDto>();
        var page = 1;
        const int pageSize = 1000;

        while (true)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.cursor.com/teams/filtered-usage-events");
            req.Headers.Authorization = BasicAuth(apiKey);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Content = JsonBody(new
            {
                startDate = from.ToUnixTimeMilliseconds(),
                endDate = to.ToUnixTimeMilliseconds(),
                page,
                pageSize
            });

            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                if (page == 1)
                {
                    var orgEvents = await FetchOrgEventsAsync(apiKey, from, to, ct);
                    if (orgEvents is not null) return orgEvents;
                }

                if ((int)res.StatusCode == 401 && text.Contains("Invalid Team API Key", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Cursor afviste nøglen som Team API Key. User API-nøgler virker ikke her — " +
                        "brug WorkosCursorSessionToken (cookie) til personligt forbrug, eller en Team Admin-nøgle (crsr_…).");

                log.LogWarning("Cursor admin filtered-usage-events failed {Status}: {Body}", (int)res.StatusCode, Truncate(text));
                throw new InvalidOperationException($"Cursor Admin API {(int)res.StatusCode}: {Truncate(text)}");
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
            req.Headers.Authorization = BasicAuth(apiKey);
            req.Content = JsonBody(new
            {
                startDate = from.ToUnixTimeMilliseconds(),
                endDate = to.ToUnixTimeMilliseconds(),
                page,
                pageSize
            });
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

    private async Task<IReadOnlyList<CursorUsageEventDto>> FetchDashboardEventsAsync(
        string sessionToken, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var cookie = NormalizeSessionCookie(sessionToken);
        var events = new List<CursorUsageEventDto>();
        var page = 1;
        const int pageSize = 1000;
        int? total = null;

        while (true)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://cursor.com/api/dashboard/get-filtered-usage-events");
            req.Headers.TryAddWithoutValidation("Cookie", $"WorkosCursorSessionToken={cookie}");
            req.Headers.TryAddWithoutValidation("Origin", "https://cursor.com");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            // Dashboard expects start/end as millisecond strings
            req.Content = JsonBody(new
            {
                startDate = from.ToUnixTimeMilliseconds().ToString(),
                endDate = to.ToUnixTimeMilliseconds().ToString(),
                page,
                pageSize
            });

            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("Cursor dashboard events failed {Status}: {Body}", (int)res.StatusCode, Truncate(text));
                if ((int)res.StatusCode is 401 or 403)
                    throw new InvalidOperationException(
                        "Cursor-session ugyldig/udløbet. Hent en frisk WorkosCursorSessionToken fra " +
                        "cursor.com/dashboard/usage (DevTools → Application → Cookies).");
                throw new InvalidOperationException($"Cursor dashboard {(int)res.StatusCode}: {Truncate(text)}");
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("totalUsageEventsCount", out var tuc) && tuc.TryGetInt32(out var n))
                total = n;

            var batch = ParseEventArray(root);
            events.AddRange(batch);

            if (batch.Count == 0) break;
            if (total is int tot && events.Count >= tot) break;
            if (batch.Count < pageSize) break;
            page++;
        }

        return events;
    }

    private async Task<IReadOnlyList<CursorLiveMeterDto>> FetchDashboardPeriodMetersAsync(
        string sessionToken, CancellationToken ct)
    {
        var cookie = NormalizeSessionCookie(sessionToken);

        // Prefer dashboard get-current-period-usage (same shape as Connect-RPC)
        using (var req = new HttpRequestMessage(HttpMethod.Post,
                   "https://cursor.com/api/dashboard/get-current-period-usage"))
        {
            req.Headers.TryAddWithoutValidation("Cookie", $"WorkosCursorSessionToken={cookie}");
            req.Headers.TryAddWithoutValidation("Origin", "https://cursor.com");
            req.Content = JsonBody(new { });
            using var res = await http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode)
            {
                var text = await res.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(text);
                var meters = ParsePeriodMeters(doc.RootElement);
                if (meters.Count > 0) return meters;
            }
        }

        using var summaryReq = new HttpRequestMessage(HttpMethod.Get, "https://cursor.com/api/usage-summary");
        summaryReq.Headers.TryAddWithoutValidation("Cookie", $"WorkosCursorSessionToken={cookie}");
        using var summaryRes = await http.SendAsync(summaryReq, ct);
        if (!summaryRes.IsSuccessStatusCode) return [];
        var summaryText = await summaryRes.Content.ReadAsStringAsync(ct);
        using var summaryDoc = JsonDocument.Parse(summaryText);
        return ParseUsageSummaryMeters(summaryDoc.RootElement);
    }

    public static IReadOnlyList<CursorLiveMeterDto> ParseUsageSummaryMeters(JsonElement root)
    {
        var meters = new List<CursorLiveMeterDto>();
        DateTimeOffset? reset = null;
        if (root.TryGetProperty("billingCycleEnd", out var bce))
            reset = ReadUnixFlexible(bce);

        if (root.TryGetProperty("individualUsage", out var ind) && ind.TryGetProperty("plan", out var plan))
        {
            if (plan.TryGetProperty("autoPercentUsed", out var auto))
                meters.Add(new("Cursor-modeller", auto.GetDouble(), reset, null));
            if (plan.TryGetProperty("apiPercentUsed", out var api))
                meters.Add(new("Andre modeller", api.GetDouble(), reset, null));
            if (meters.Count == 0 && plan.TryGetProperty("totalPercentUsed", out var total))
                meters.Add(new("Måned", total.GetDouble(), reset, null));
        }

        if (root.TryGetProperty("individualUsage", out var ind2)
            && ind2.TryGetProperty("onDemand", out var od)
            && od.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True)
        {
            var used = ReadDecimal(od, "used");
            var limit = ReadDecimal(od, "limit");
            if (limit > 0)
                meters.Add(new("Spend", (double)(used / limit * 100), reset, $"${used / 100:0.##} / ${limit / 100:0.##}"));
            else if (used > 0)
                meters.Add(new("Spend", 0, reset, $"${used / 100:0.##}"));
        }

        return meters;
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
            ?? HashKey($"{ts:o}|{model}|{email}|{input}|{output}|{charged}|{conversationId}|{kind}");

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

    private static AuthenticationHeaderValue BasicAuth(string apiKey) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{apiKey}:")));

    private static StringContent JsonBody(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static string NormalizeSessionCookie(string raw)
    {
        var c = raw.Trim();
        // Allow pasting "WorkosCursorSessionToken=..." or URL-encoded form
        if (c.StartsWith("WorkosCursorSessionToken=", StringComparison.OrdinalIgnoreCase))
            c = c["WorkosCursorSessionToken=".Length..];
        // Cookie header values with :: are fine; if user pasted decoded JWT after user_id::
        return c;
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
