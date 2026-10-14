using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AiSpend.Api.Services;

public sealed record CodexDailyBucketDto(
    string ExternalKey,
    DateOnly Day,
    long Tokens,
    string RawJson);

public sealed record CodexUsageSnapshotDto(
    IReadOnlyList<CursorLiveMeterDto> Meters,
    IReadOnlyList<CodexDailyBucketDto> DailyBuckets,
    long? LifetimeTokens,
    string? PlanType,
    string RawUsageJson,
    string? RawProfileJson);

public sealed class CodexUsageClient(HttpClient http, ILogger<CodexUsageClient> log)
{
    public async Task ValidateCredentialAsync(string cookieCredential, CancellationToken ct = default)
    {
        var cookie = NormalizeCookieHeader(cookieCredential);
        _ = await ExchangeSessionAsync(cookie, ct);
    }

    public async Task<CodexUsageSnapshotDto> FetchAsync(string cookieCredential, CancellationToken ct = default)
    {
        var cookie = NormalizeCookieHeader(cookieCredential);
        var (accessToken, accountId) = await ExchangeSessionAsync(cookie, ct);

        var usageJson = await GetAuthorizedAsync(
            "https://chatgpt.com/backend-api/wham/usage",
            accessToken, accountId, cookie, "https://chatgpt.com/codex/settings/usage", ct);

        using var usageDoc = JsonDocument.Parse(usageJson);
        var meters = ParseUsageMeters(usageDoc.RootElement);
        var planType = usageDoc.RootElement.TryGetProperty("plan_type", out var pt)
            && pt.ValueKind == JsonValueKind.String ? pt.GetString() : null;

        string? profileJson = null;
        IReadOnlyList<CodexDailyBucketDto> buckets = [];
        long? lifetime = null;
        try
        {
            profileJson = await GetAuthorizedAsync(
                "https://chatgpt.com/backend-api/wham/profiles/me",
                accessToken, accountId, cookie, "https://chatgpt.com/codex/settings/usage", ct);
            using var profileDoc = JsonDocument.Parse(profileJson);
            (buckets, lifetime) = ParseProfileBuckets(profileDoc.RootElement);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Codex profile/token-buckets kunne ikke hentes (rate-limits er stadig OK)");
        }

        return new CodexUsageSnapshotDto(meters, buckets, lifetime, planType, usageJson, profileJson);
    }

    private async Task<(string AccessToken, string? AccountId)> ExchangeSessionAsync(
        string cookieHeader, CancellationToken ct)
    {
        log.LogInformation("ChatGPT session exchange cookieHeaderLen={Len} preview={Preview}",
            cookieHeader.Length, Truncate(cookieHeader.Split('=')[0] + (cookieHeader.Contains('=') ? "=…" : "")));

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/api/auth/session");
        req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");

        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("ChatGPT session failed {Status}: {Body}", (int)res.StatusCode, Truncate(text));
            throw new InvalidOperationException(
                "ChatGPT-session ugyldig/udløbet. Log ind på chatgpt.com og kopiér kun cookie-værdien for " +
                "__Secure-next-auth.session-token (eller begge .0/.1 som navn=værdi; navn=værdi).");
        }

        if (string.IsNullOrWhiteSpace(text) || text.Trim() is "null" or "undefined" or "{}")
        {
            throw new InvalidOperationException(
                "ChatGPT returnerede ingen session (null). Cookien er forkert formateret eller udløbet. " +
                "Kopiér kun Value-feltet for __Secure-next-auth.session-token — ikke cookie-navnet. " +
                "Hvis cookien er sharded: indsæt `__Secure-next-auth.session-token.0=…; __Secure-next-auth.session-token.1=…`.");
        }

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"ChatGPT-session returnerede {root.ValueKind} i stedet for et objekt. " +
                "Tjek at cookien er den fulde Value (JWE der starter med eyJ…) uden ekstra tekst.");
        }

        if (!root.TryGetProperty("accessToken", out var at) || at.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(at.GetString()))
        {
            throw new InvalidOperationException(
                "ChatGPT-session returnerede ingen accessToken. Er cookien komplet (inkl. .0/.1 hvis sharded)?");
        }

        string? accountId = null;
        if (root.TryGetProperty("account", out var acc) && acc.ValueKind == JsonValueKind.Object)
        {
            if (acc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                accountId = id.GetString();
            else if (acc.TryGetProperty("account_id", out var aid) && aid.ValueKind == JsonValueKind.String)
                accountId = aid.GetString();
        }
        if (accountId is null && root.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
            && user.TryGetProperty("id", out var uid) && uid.ValueKind == JsonValueKind.String)
            accountId = uid.GetString();

        return (at.GetString()!, accountId);
    }

    private async Task<string> GetAuthorizedAsync(
        string url, string accessToken, string? accountId, string cookieHeader, string referer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        req.Headers.TryAddWithoutValidation("Referer", referer);
        req.Headers.TryAddWithoutValidation("Origin", "https://chatgpt.com");
        req.Headers.TryAddWithoutValidation("User-Agent", "AI-Spend/1.0");
        if (!string.IsNullOrWhiteSpace(accountId))
            req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);

        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("Codex GET {Url} failed {Status}: {Body}", url, (int)res.StatusCode, Truncate(text));
            if ((int)res.StatusCode is 401 or 403)
                throw new InvalidOperationException(
                    "Codex/ChatGPT afviste token (401/403). Opdatér session-cookien fra chatgpt.com.");
            throw new InvalidOperationException($"Codex API {(int)res.StatusCode}: {Truncate(text)}");
        }
        return text;
    }

    public static IReadOnlyList<CursorLiveMeterDto> ParseUsageMeters(JsonElement root)
    {
        var meters = new List<CursorLiveMeterDto>();
        if (root.ValueKind != JsonValueKind.Object) return meters;
        var rate = root.TryGetProperty("rate_limit", out var rl) && rl.ValueKind == JsonValueKind.Object ? rl : root;

        if (rate.TryGetProperty("primary_window", out var primary))
            meters.Add(ParseWindow(primary, "5 timer", 18000));
        if (rate.TryGetProperty("secondary_window", out var secondary))
            meters.Add(ParseWindow(secondary, "Uge", 604800));

        // Fallback nested under rate_limit already handled; also accept direct windows
        if (meters.Count == 0 && root.TryGetProperty("primary_window", out var p2))
            meters.Add(ParseWindow(p2, "5 timer", 18000));
        if (meters.Count == 0 && root.TryGetProperty("secondary_window", out var s2))
            meters.Add(ParseWindow(s2, "Uge", 604800));

        if (root.TryGetProperty("credits", out var credits))
        {
            var has = credits.TryGetProperty("has_credits", out var hc) && hc.ValueKind == JsonValueKind.True;
            var unlimited = credits.TryGetProperty("unlimited", out var un) && un.ValueKind == JsonValueKind.True;
            var balance = ReadDecimal(credits, "balance");
            if (unlimited)
                meters.Add(new("Credits", 0, null, "unlimited"));
            else if (has || balance > 0)
                meters.Add(new("Credits", 0, null, balance.ToString("0.##", CultureInfo.InvariantCulture)));
        }

        if (root.TryGetProperty("code_review_rate_limit", out var cr)
            && cr.TryGetProperty("primary_window", out var crw))
            meters.Add(ParseWindow(crw, "Code review", 604800));

        return meters;
    }

    private static CursorLiveMeterDto ParseWindow(JsonElement window, string defaultLabel, int defaultSeconds)
    {
        var used = window.TryGetProperty("used_percent", out var up)
            ? (up.ValueKind == JsonValueKind.Number ? up.GetDouble() : 0)
            : 0;

        var seconds = window.TryGetProperty("limit_window_seconds", out var lws) && lws.TryGetInt32(out var sec)
            ? sec : defaultSeconds;
        var label = seconds switch
        {
            18000 => "5 timer",
            604800 => "Uge",
            86400 => "Dag",
            _ when seconds % 60 == 0 && seconds / 60 == 300 => "5 timer",
            _ when seconds % 60 == 0 && seconds / 60 == 10080 => "Uge",
            _ => defaultLabel
        };

        DateTimeOffset? reset = null;
        if (window.TryGetProperty("reset_at", out var ra))
            reset = ReadUnixFlexible(ra);
        else if (window.TryGetProperty("reset_after_seconds", out var ras) && ras.TryGetInt32(out var after))
            reset = DateTimeOffset.UtcNow.AddSeconds(after);

        return new CursorLiveMeterDto(label, used, reset, null);
    }

    public static (IReadOnlyList<CodexDailyBucketDto> Buckets, long? Lifetime) ParseProfileBuckets(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return ([], null);
        var stats = root.TryGetProperty("stats", out var s) && s.ValueKind == JsonValueKind.Object ? s : root;
        long? lifetime = null;
        if (stats.TryGetProperty("lifetime_tokens", out var lt) && lt.TryGetInt64(out var life))
            lifetime = life;

        JsonElement? arr = null;
        if (stats.TryGetProperty("daily_usage_buckets", out var dub)) arr = dub;
        else if (stats.TryGetProperty("last_three_buckets", out var l3)) arr = l3;
        else if (stats.TryGetProperty("dailyUsageBuckets", out var dub2)) arr = dub2;

        if (arr is null || arr.Value.ValueKind != JsonValueKind.Array)
            return ([], lifetime);

        var list = new List<CodexDailyBucketDto>();
        foreach (var item in arr.Value.EnumerateArray())
        {
            var dayStr = GetString(item, "start_date") ?? GetString(item, "startDate");
            if (dayStr is null || !DateOnly.TryParse(dayStr, out var day)) continue;
            var tokens = ReadLong(item, "tokens");
            list.Add(new CodexDailyBucketDto(
                $"codex-day|{day:yyyy-MM-dd}",
                day,
                tokens,
                item.GetRawText()));
        }

        return (list, lifetime);
    }

    /// <summary>
    /// Accept bare token, single cookie assignment, or full Cookie header (sharded .0/.1).
    /// Also cleans common paste mistakes like "VALUE CookieName" or "CookieName VALUE".
    /// </summary>
    public static string NormalizeCookieHeader(string raw)
    {
        var c = raw.Trim().Trim('"').Trim('\'');
        if (c.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
            c = c["Cookie:".Length..].Trim();

        c = StripAccidentalCookieName(c);

        // Full Cookie header with session-token name=value (possibly sharded)
        if (c.Contains("__Secure-next-auth.session-token", StringComparison.OrdinalIgnoreCase)
            || c.Contains("next-auth.session-token", StringComparison.OrdinalIgnoreCase))
        {
            var parts = c.Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => p.Contains('='))
                .ToList();
            if (parts.Count > 0)
                return string.Join("; ", parts);
        }

        // Bare JWE/JWT value — pick the eyJ… token if present
        var tokens = c.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var value = tokens.FirstOrDefault(t => t.StartsWith("eyJ", StringComparison.Ordinal))
            ?? tokens.FirstOrDefault()
            ?? c;
        return $"__Secure-next-auth.session-token={value}";
    }

    private static string StripAccidentalCookieName(string c)
    {
        const string name = "__Secure-next-auth.session-token";
        // "eyJ… __Secure-next-auth.session-token"
        if (c.EndsWith(name, StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = c[..^name.Length].Trim();
            if (trimmed.Length > 0 && !trimmed.Contains('='))
                return trimmed;
        }
        // "__Secure-next-auth.session-token eyJ…"
        if (c.StartsWith(name, StringComparison.OrdinalIgnoreCase)
            && c.Length > name.Length
            && c[name.Length] is ' ' or '\t')
        {
            var rest = c[(name.Length)..].Trim();
            if (rest.StartsWith("eyJ", StringComparison.Ordinal) && !rest.Contains('='))
                return rest;
        }
        // Same for .0 / .1 names pasted alone after value
        foreach (var suffix in new[] { ".0", ".1" })
        {
            var n = name + suffix;
            if (c.EndsWith(n, StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = c[..^n.Length].Trim();
                if (trimmed.Length > 0 && !trimmed.Contains('='))
                    return trimmed;
            }
        }
        return c;
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
            if (p.ValueKind == JsonValueKind.String
                && decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s))
                return s;
        }
        return 0;
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
