using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
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
    private const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public async Task ValidateCredentialAsync(string cookieCredential, CancellationToken ct = default)
    {
        // Rør faktisk wham/usage — ellers gemmes ugyldige tokens uden fejl.
        _ = await FetchAsync(cookieCredential, ct);
    }

    public async Task<CodexUsageSnapshotDto> FetchAsync(string cookieCredential, CancellationToken ct = default)
    {
        var auth = ResolveAuth(cookieCredential);
        var (accessToken, accountId) = await EnsureAccessTokenAsync(auth, ct);
        accountId ??= TryReadAccountIdFromJwt(accessToken);

        var usageJson = await GetAuthorizedAsync(
            "https://chatgpt.com/backend-api/wham/usage",
            accessToken, accountId, auth.CookieHeader, "https://chatgpt.com/codex/settings/usage", ct);

        if (string.IsNullOrWhiteSpace(usageJson) || usageJson.Trim() is "null" or "undefined" or "{}")
            throw new InvalidOperationException(
                "Codex usage returnerede tomt svar. Token kan mangle Codex-adgang — kør `codex login` igen.");

        using var usageDoc = JsonDocument.Parse(usageJson);
        if (usageDoc.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                $"Codex usage returnerede {usageDoc.RootElement.ValueKind} i stedet for JSON-objekt.");

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
                accessToken, accountId, auth.CookieHeader, "https://chatgpt.com/codex/settings/usage", ct);
            if (!string.IsNullOrWhiteSpace(profileJson) && profileJson.Trim() is not ("null" or "undefined"))
            {
                using var profileDoc = JsonDocument.Parse(profileJson);
                if (profileDoc.RootElement.ValueKind == JsonValueKind.Object)
                    (buckets, lifetime) = ParseProfileBuckets(profileDoc.RootElement);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Codex profile/token-buckets kunne ikke hentes (rate-limits er stadig OK)");
        }

        return new CodexUsageSnapshotDto(meters, buckets, lifetime, planType, usageJson, profileJson);
    }

    private async Task<(string AccessToken, string? AccountId)> EnsureAccessTokenAsync(
        CodexAuth auth, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(auth.AccessToken))
            return (auth.AccessToken, auth.AccountId);

        if (string.IsNullOrWhiteSpace(auth.CookieHeader))
            throw new InvalidOperationException(
                "Mangler ChatGPT-session-cookie eller Codex access_token (fra ~/.codex/auth.json).");

        return await ExchangeSessionAsync(auth.CookieHeader, ct);
    }

    private async Task<(string AccessToken, string? AccountId)> ExchangeSessionAsync(
        string cookieHeader, CancellationToken ct)
    {
        log.LogInformation("ChatGPT session exchange cookieHeaderLen={Len} preview={Preview}",
            cookieHeader.Length, Truncate(cookieHeader.Split('=')[0] + (cookieHeader.Contains('=') ? "=…" : "")));

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/api/auth/session");
        ApplyBrowserHeaders(req, "https://chatgpt.com/", cookieHeader);

        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("ChatGPT session failed {Status}: {Body}", (int)res.StatusCode, Truncate(text));
            throw CloudOrSessionError((int)res.StatusCode, text, cookiePath: true);
        }

        if (string.IsNullOrWhiteSpace(text) || text.Trim() is "null" or "undefined" or "{}")
        {
            throw new InvalidOperationException(
                "ChatGPT returnerede ingen session (null/{}). Cookien er forkert formateret eller udløbet. " +
                "Kopiér Value for __Secure-next-auth.session-token — eller paste tokens.access_token fra ~/.codex/auth.json.");
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
        string url, string accessToken, string? accountId, string? cookieHeader, string referer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        ApplyBrowserHeaders(req, referer, cookieHeader);
        if (!string.IsNullOrWhiteSpace(accountId))
            req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);

        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("Codex GET {Url} failed {Status}: {Body}", url, (int)res.StatusCode, Truncate(text));
            if ((int)res.StatusCode is 401 or 403)
                throw CloudOrSessionError((int)res.StatusCode, text, cookiePath: false);
            throw new InvalidOperationException($"Codex API {(int)res.StatusCode}: {Truncate(text)}");
        }
        return text;
    }

    private static void ApplyBrowserHeaders(HttpRequestMessage req, string referer, string? cookieHeader)
    {
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9,da;q=0.8");
        req.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
        req.Headers.TryAddWithoutValidation("Referer", referer);
        req.Headers.TryAddWithoutValidation("Origin", "https://chatgpt.com");
        req.Headers.TryAddWithoutValidation("sec-ch-ua",
            "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"");
        req.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        req.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        if (!string.IsNullOrWhiteSpace(cookieHeader))
            req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
    }

    private static InvalidOperationException CloudOrSessionError(int status, string body, bool cookiePath)
    {
        var looksLikeCf = status == 403 && (
            body.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)
            || body.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
            || body.Contains("Attention Required", StringComparison.OrdinalIgnoreCase)
            || body.TrimStart().StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || body.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase));

        if (looksLikeCf)
        {
            return new InvalidOperationException(
                "Cloudflare blokerer serverens kald til chatgpt.com (403). " +
                "Session-cookie virker ofte ikke fra hosting-IP. " +
                "Brug i stedet Codex CLI-token: åbn ~/.codex/auth.json og paste hele filen " +
                "(eller kun tokens.access_token). Kør `codex login` først hvis filen mangler.");
        }

        if (cookiePath)
        {
            return new InvalidOperationException(
                $"ChatGPT-session afvist ({status}). Log ind på chatgpt.com og kopiér cookie-værdien, " +
                "eller paste ~/.codex/auth.json (anbefalet).");
        }

        return new InvalidOperationException(
            $"Codex/ChatGPT afviste token ({status}). Opdatér access_token via `codex login` eller frisk cookie.");
    }

    /// <summary>
    /// Accept bare session JWE, Cookie header (sharded), Codex access_token JWT, or full auth.json.
    /// </summary>
    public static CodexAuth ResolveAuth(string raw)
    {
        var c = raw.Trim().Trim('"').Trim('\'');
        if (c.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
            c = c["Cookie:".Length..].Trim();

        // Full Codex CLI auth.json
        if (c.StartsWith('{') && c.Contains("access_token", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(c);
                var root = doc.RootElement;
                var tokens = root.TryGetProperty("tokens", out var t) && t.ValueKind == JsonValueKind.Object
                    ? t : root;
                var access = GetJsonString(tokens, "access_token")
                    ?? GetJsonString(root, "access_token");
                var account = GetJsonString(tokens, "account_id")
                    ?? GetJsonString(root, "account_id");
                if (!string.IsNullOrWhiteSpace(access))
                    return new CodexAuth(access, account, CookieHeader: null);
            }
            catch (JsonException)
            {
                // fall through
            }
        }

        // access_token=…; account_id=…  or bare JWT (3 segments, not JWE)
        var asAssignments = ParseAssignments(c);
        if (asAssignments.TryGetValue("access_token", out var at) && LooksLikeJwt(at))
        {
            asAssignments.TryGetValue("account_id", out var aid);
            return new CodexAuth(at, aid, CookieHeader: null);
        }

        if (LooksLikeJwt(c.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? c))
        {
            var jwt = c.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0];
            return new CodexAuth(jwt, AccountId: null, CookieHeader: null);
        }

        c = StripAccidentalCookieName(c);

        // Cookie header — keep session shards + useful extras (cf_clearance, oai-did, …)
        if (c.Contains("session-token", StringComparison.OrdinalIgnoreCase)
            || c.Contains("cf_clearance", StringComparison.OrdinalIgnoreCase)
            || c.Contains('='))
        {
            var parts = c.Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(p => p.Contains('='))
                .ToList();
            if (parts.Count > 0 && parts.Any(p =>
                    p.Contains("session-token", StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith("cf_clearance=", StringComparison.OrdinalIgnoreCase)))
            {
                // If only a huge bare value was wrapped elsewhere, handle below
                if (parts.Any(p => p.Contains("session-token", StringComparison.OrdinalIgnoreCase)))
                    return new CodexAuth(null, null, string.Join("; ", parts));
            }
        }

        // Bare JWE/JWT value — NextAuth session cookie
        var tokens2 = c.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var value = tokens2.FirstOrDefault(t => t.StartsWith("eyJ", StringComparison.Ordinal))
            ?? tokens2.FirstOrDefault()
            ?? c;

        // Large sessions are stored sharded in the browser; send as .0/.1 when over chunk size
        const int chunkSize = 3900;
        if (value.Length > chunkSize)
        {
            var chunks = new List<string>();
            for (var i = 0; i * chunkSize < value.Length; i++)
            {
                var len = Math.Min(chunkSize, value.Length - i * chunkSize);
                chunks.Add($"__Secure-next-auth.session-token.{i}={value.Substring(i * chunkSize, len)}");
            }
            return new CodexAuth(null, null, string.Join("; ", chunks));
        }

        return new CodexAuth(null, null, $"__Secure-next-auth.session-token={value}");
    }

    /// <summary>Backward-compatible wrapper used by tests/callers expecting a Cookie header string. </summary>
    public static string NormalizeCookieHeader(string raw)
    {
        var auth = ResolveAuth(raw);
        if (!string.IsNullOrWhiteSpace(auth.CookieHeader))
            return auth.CookieHeader!;
        if (!string.IsNullOrWhiteSpace(auth.AccessToken))
            return $"access_token={auth.AccessToken}"
                + (string.IsNullOrWhiteSpace(auth.AccountId) ? "" : $"; account_id={auth.AccountId}");
        return raw.Trim();
    }

    public sealed record CodexAuth(string? AccessToken, string? AccountId, string? CookieHeader);

    private static Dictionary<string, string> ParseAssignments(string c)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in c.Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            dict[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        return dict;
    }

    private static bool LooksLikeJwt(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith("eyJ", StringComparison.Ordinal))
            return false;
        // JWE (next-auth encrypted cookie): header.encryptedKey.iv.ciphertext.tag — often empty encryptedKey ("..")
        // JWT access_token: header.payload.signature (exactly 3 segments, no empty middle)
        var parts = token.Split('.');
        if (parts.Length != 3) return false;
        if (parts.Any(string.IsNullOrEmpty)) return false;
        // Reject JWE alg "dir" session cookies mistaken as JWT
        try
        {
            var json = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
            if (json.Contains("\"enc\"", StringComparison.Ordinal) || json.Contains("\"dir\"", StringComparison.Ordinal))
                return false;
        }
        catch
        {
            return false;
        }
        return true;
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private static string? GetJsonString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    /// <summary>Læs chatgpt_account_id fra access_token JWT-claims hvis auth.json mangler account_id.</summary>
    private static string? TryReadAccountIdFromJwt(string accessToken)
    {
        try
        {
            var parts = accessToken.Split('.');
            if (parts.Length < 2) return null;
            var json = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("https://api.openai.com/auth", out var auth)
                && auth.ValueKind == JsonValueKind.Object)
            {
                var id = GetJsonString(auth, "chatgpt_account_id")
                    ?? GetJsonString(auth, "account_id");
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }
            return GetJsonString(root, "chatgpt_account_id")
                ?? GetJsonString(root, "account_id");
        }
        catch
        {
            return null;
        }
    }

    private static string StripAccidentalCookieName(string c)
    {
        const string name = "__Secure-next-auth.session-token";
        if (c.EndsWith(name, StringComparison.OrdinalIgnoreCase))
        {
            var trimmed = c[..^name.Length].Trim();
            if (trimmed.Length > 0 && !trimmed.Contains('='))
                return trimmed;
        }
        if (c.StartsWith(name, StringComparison.OrdinalIgnoreCase)
            && c.Length > name.Length
            && c[name.Length] is ' ' or '\t')
        {
            var rest = c[(name.Length)..].Trim();
            if (rest.StartsWith("eyJ", StringComparison.Ordinal) && !rest.Contains('='))
                return rest;
        }
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

    public static IReadOnlyList<CursorLiveMeterDto> ParseUsageMeters(JsonElement root)
    {
        var meters = new List<CursorLiveMeterDto>();
        if (root.ValueKind != JsonValueKind.Object) return meters;
        var rate = root.TryGetProperty("rate_limit", out var rl) && rl.ValueKind == JsonValueKind.Object ? rl : root;

        // ChatGPT sætter ofte primary_window/secondary_window til null — må ikke kalde TryGetProperty på Null.
        if (TryGetObject(rate, "primary_window", out var primary))
            meters.Add(ParseWindow(primary, "5 timer", 18000));
        if (TryGetObject(rate, "secondary_window", out var secondary))
            meters.Add(ParseWindow(secondary, "Uge", 604800));

        if (meters.Count == 0 && TryGetObject(root, "primary_window", out var p2))
            meters.Add(ParseWindow(p2, "5 timer", 18000));
        if (meters.Count == 0 && TryGetObject(root, "secondary_window", out var s2))
            meters.Add(ParseWindow(s2, "Uge", 604800));

        if (TryGetObject(root, "credits", out var credits))
        {
            var has = credits.TryGetProperty("has_credits", out var hc) && hc.ValueKind == JsonValueKind.True;
            var unlimited = credits.TryGetProperty("unlimited", out var un) && un.ValueKind == JsonValueKind.True;
            var balance = ReadDecimal(credits, "balance");
            if (unlimited)
                meters.Add(new("Credits", 0, null, "unlimited"));
            else if (has || balance > 0)
                meters.Add(new("Credits", 0, null, balance.ToString("0.##", CultureInfo.InvariantCulture)));
        }

        if (TryGetObject(root, "code_review_rate_limit", out var cr)
            && TryGetObject(cr, "primary_window", out var crw))
            meters.Add(ParseWindow(crw, "Code review", 604800));

        return meters;
    }

    private static bool TryGetObject(JsonElement parent, string name, out JsonElement obj)
    {
        obj = default;
        if (parent.ValueKind != JsonValueKind.Object) return false;
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object) return false;
        obj = el;
        return true;
    }

    private static CursorLiveMeterDto ParseWindow(JsonElement window, string defaultLabel, int defaultSeconds)
    {
        if (window.ValueKind != JsonValueKind.Object)
            return new CursorLiveMeterDto(defaultLabel, 0, null, null);

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
