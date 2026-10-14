using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AiSpend.Api.Services;

public sealed record OpenAiUsageBucketDto(
    string ExternalKey,
    DateTimeOffset OccurredAt,
    string? Model,
    string? ProjectId,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long RequestCount,
    string RawJson);

public sealed record OpenAiCostBucketDto(
    string ExternalKey,
    DateTimeOffset DayStart,
    decimal AmountUsd,
    string? OrganizationName,
    string RawJson);

public sealed class OpenAiUsageClient(HttpClient http, ILogger<OpenAiUsageClient> log)
{
    public async Task<IReadOnlyList<OpenAiUsageBucketDto>> FetchCompletionsUsageAsync(
        string adminKey,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken ct = default)
    {
        var results = new List<OpenAiUsageBucketDto>();
        string? after = null;
        do
        {
            var url =
                $"https://api.openai.com/v1/organization/usage/completions" +
                $"?start_time={start.ToUnixTimeSeconds()}" +
                $"&end_time={end.ToUnixTimeSeconds()}" +
                $"&bucket_width=1d&group_by=model&limit=31";
            if (!string.IsNullOrEmpty(after)) url += $"&page={Uri.EscapeDataString(after)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);
            using var res = await http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("OpenAI usage failed {Status}: {Body}", (int)res.StatusCode, text[..Math.Min(300, text.Length)]);
                throw new InvalidOperationException($"OpenAI usage {(int)res.StatusCode}: {text[..Math.Min(300, text.Length)]}");
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data))
            {
                foreach (var bucket in data.EnumerateArray())
                {
                    var startUnix = bucket.TryGetProperty("start_time", out var st) ? st.GetInt64() : start.ToUnixTimeSeconds();
                    var day = DateTimeOffset.FromUnixTimeSeconds(startUnix);
                    if (!bucket.TryGetProperty("results", out var rows)) continue;
                    foreach (var row in rows.EnumerateArray())
                    {
                        var model = GetString(row, "model") ?? "unknown";
                        var projectId = GetString(row, "project_id");
                        var input = ReadLong(row, "input_tokens");
                        var output = ReadLong(row, "output_tokens");
                        var cached = ReadLong(row, "input_cached_tokens");
                        var requests = ReadLong(row, "num_model_requests");
                        var key = $"oa-comp|{day:yyyy-MM-dd}|{model}|{projectId}";
                        results.Add(new OpenAiUsageBucketDto(
                            key, day, model, projectId, input, output, cached, requests, row.GetRawText()));
                    }
                }
            }

            after = root.TryGetProperty("next_page", out var np) && np.ValueKind == JsonValueKind.String
                ? np.GetString() : null;
        } while (!string.IsNullOrEmpty(after));

        return results;
    }

    public async Task<IReadOnlyList<OpenAiCostBucketDto>> FetchCostsAsync(
        string adminKey,
        DateTimeOffset monthStart,
        CancellationToken ct = default)
    {
        var url =
            $"https://api.openai.com/v1/organization/costs" +
            $"?start_time={monthStart.ToUnixTimeSeconds()}&limit=31&bucket_width=1d";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenAI costs {(int)res.StatusCode}: {text[..Math.Min(300, text.Length)]}");

        using var doc = JsonDocument.Parse(text);
        var list = new List<OpenAiCostBucketDto>();
        if (!doc.RootElement.TryGetProperty("data", out var data)) return list;

        foreach (var bucket in data.EnumerateArray())
        {
            var startUnix = bucket.GetProperty("start_time").GetInt64();
            var day = DateTimeOffset.FromUnixTimeSeconds(startUnix);
            if (!bucket.TryGetProperty("results", out var rows)) continue;
            decimal sum = 0;
            string? orgName = null;
            foreach (var row in rows.EnumerateArray())
            {
                if (row.TryGetProperty("organization_name", out var on) && on.ValueKind == JsonValueKind.String)
                    orgName ??= on.GetString();
                if (row.TryGetProperty("amount", out var amount) && amount.TryGetProperty("value", out var val))
                {
                    if (val.ValueKind == JsonValueKind.Number) sum += val.GetDecimal();
                    else if (val.ValueKind == JsonValueKind.String &&
                             decimal.TryParse(val.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                        sum += d;
                }
            }
            list.Add(new OpenAiCostBucketDto($"oa-cost|{day:yyyy-MM-dd}", day, sum, orgName, bucket.GetRawText()));
        }
        return list;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long ReadLong(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return 0;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v)) return v;
        if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), out var s)) return s;
        return 0;
    }
}
