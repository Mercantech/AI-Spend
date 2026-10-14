using System.Security.Claims;
using AiSpend.Api.Data;
using AiSpend.Api.Dtos;
using AiSpend.Api.Models;
using AiSpend.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration["CONNECTION_STRING"]
    ?? builder.Configuration.GetConnectionString("Default")
    ?? "Host=localhost;Port=5432;Database=aispend;Username=aispend;Password=aispend";

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));

var authIssuer = builder.Configuration["AUTH_ISSUER"] ?? "https://auth.mercantec.tech";
var authAudience = builder.Configuration["AUTH_AUDIENCE"] ?? "mercantec-apps";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authIssuer;
        options.Audience = authAudience;
        options.RequireHttpsMetadata = true;
        options.MapInboundClaims = false;
        options.MetadataAddress = $"{authIssuer.TrimEnd('/')}/.well-known/openid-configuration";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authIssuer,
            ValidateAudience = true,
            ValidAudience = authAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            NameClaimType = "sub",
            RoleClaimType = "role",
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        options.Events = new JwtBearerEvents
        {
            OnChallenge = ctx =>
            {
                ctx.HandleResponse();
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.ContentType = "application/json";
                return ctx.Response.WriteAsync("""{"error":"unauthorized"}""");
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();

var corsOrigins = (builder.Configuration["CORS_ORIGIN"] ?? "http://localhost:5173")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddHttpClient<CursorUsageClient>(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient<OpenAiUsageClient>(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient<CodexUsageClient>(c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<CredentialProtector>();
builder.Services.AddScoped<OrgAccessService>();
builder.Services.AddScoped<UsageIngestService>();
builder.Services.AddHostedService<IngestBackgroundService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    for (var i = 0; i < 30; i++)
    {
        try
        {
            await db.Database.EnsureCreatedAsync();
            break;
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "DB EnsureCreated attempt {Attempt}", i + 1);
            await Task.Delay(2000);
        }
    }
}

app.UseCors();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    try { await next(); }
    catch (UnauthorizedAccessException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (ArgumentException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});
app.UseAuthentication();
app.UseAuthorization();

static Guid RequireUserSub(ClaimsPrincipal user)
{
    var sub = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
    if (!Guid.TryParse(sub, out var id)) throw new UnauthorizedAccessException("Ugyldigt sub");
    return id;
}

static (DateTimeOffset From, DateTimeOffset To) ParseRange(HttpRequest req)
{
    var to = DateTimeOffset.TryParse(req.Query["to"], out var t) ? t.ToUniversalTime() : DateTimeOffset.UtcNow;
    var from = DateTimeOffset.TryParse(req.Query["from"], out var f)
        ? f.ToUniversalTime()
        : to.AddDays(-30);
    return (from, to);
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "ai-spend" }));

app.MapGet("/api/me/orgs", async (ClaimsPrincipal user, OrgAccessService orgs, AppDbContext db) =>
{
    var sub = RequireUserSub(user);
    await orgs.EnsurePersonalOrgAsync(sub);
    var list = await db.Memberships
        .Where(m => m.UserSub == sub)
        .Select(m => new OrgDto(m.OrganizationId, m.Organization.Name, m.Role.ToString()))
        .ToListAsync();
    return Results.Ok(list);
}).RequireAuthorization();

app.MapGet("/api/connections", async (ClaimsPrincipal user, Guid? organizationId, OrgAccessService orgs, AppDbContext db) =>
{
    var sub = RequireUserSub(user);
    var org = await orgs.GetOrgForUserAsync(sub, organizationId)
        ?? throw new UnauthorizedAccessException("Organisation ikke fundet");
    var items = await db.ProviderConnections
        .Where(c => c.OrganizationId == org.Id)
        .OrderBy(c => c.Provider)
        .Select(c => new ConnectionDto(
            c.Id, DtoMaps.ProviderName(c.Provider), c.Scope.ToString().ToLowerInvariant(),
            c.Label, c.IsActive, c.LastSyncAt, c.LastSyncError, c.EncryptedCredential != null))
        .ToListAsync();
    return Results.Ok(items);
}).RequireAuthorization();

app.MapPost("/api/connections", async (ClaimsPrincipal user, CreateConnectionRequest body, Guid? organizationId,
    OrgAccessService orgs, AppDbContext db, CredentialProtector protector,
    CursorUsageClient cursorClient, CodexUsageClient codexClient) =>
{
    var sub = RequireUserSub(user);
    var org = await orgs.GetOrgForUserAsync(sub, organizationId)
        ?? throw new UnauthorizedAccessException("Organisation ikke fundet");
    await orgs.RequireAdminAsync(sub, org.Id);

    if (string.IsNullOrWhiteSpace(body.ApiKey))
        return Results.BadRequest(new { error = "ApiKey er påkrævet" });

    var provider = DtoMaps.ParseProvider(body.Provider);
    var secret = body.ApiKey.Trim();
    if (provider == ProviderType.Cursor)
        await cursorClient.ValidateCredentialAsync(secret);
    else if (provider == ProviderType.Codex)
        await codexClient.ValidateCredentialAsync(secret);

    var conn = new ProviderConnection
    {
        OrganizationId = org.Id,
        Provider = provider,
        Scope = DtoMaps.ParseScope(body.Scope),
        Label = string.IsNullOrWhiteSpace(body.Label) ? provider.ToString() : body.Label.Trim(),
        EncryptedCredential = protector.Encrypt(secret),
        IsActive = true
    };
    db.ProviderConnections.Add(conn);
    await db.SaveChangesAsync();
    return Results.Created($"/api/connections/{conn.Id}", new ConnectionDto(
        conn.Id, DtoMaps.ProviderName(conn.Provider), conn.Scope.ToString().ToLowerInvariant(),
        conn.Label, conn.IsActive, null, null, true));
}).RequireAuthorization();

app.MapDelete("/api/connections/{id:guid}", async (Guid id, ClaimsPrincipal user, OrgAccessService orgs, AppDbContext db) =>
{
    var sub = RequireUserSub(user);
    var conn = await db.ProviderConnections.FirstOrDefaultAsync(c => c.Id == id);
    if (conn is null) return Results.NotFound();
    await orgs.RequireAdminAsync(sub, conn.OrganizationId);
    db.ProviderConnections.Remove(conn);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/ingest/cursor/backfill", async (ClaimsPrincipal user, BackfillRequest body,
    OrgAccessService orgs, AppDbContext db, UsageIngestService ingest) =>
{
    var sub = RequireUserSub(user);
    var org = await orgs.GetOrgForUserAsync(sub, body.OrganizationId)
        ?? throw new UnauthorizedAccessException("Organisation ikke fundet");
    await orgs.RequireAdminAsync(sub, org.Id);

    var conn = body.ConnectionId is Guid cid
        ? await db.ProviderConnections.FirstOrDefaultAsync(c => c.Id == cid && c.OrganizationId == org.Id)
        : await db.ProviderConnections.FirstOrDefaultAsync(c =>
            c.OrganizationId == org.Id && c.Provider == ProviderType.Cursor && c.IsActive);
    if (conn is null) return Results.BadRequest(new { error = "Ingen Cursor-forbindelse" });

    var to = body.To?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
    var from = body.From?.ToUniversalTime() ?? to.AddDays(-30);
    var (upserted, raw) = await ingest.IngestCursorAsync(conn, from, to);
    return Results.Ok(new { upserted, rawStored = raw, from, to, connectionId = conn.Id });
}).RequireAuthorization();

app.MapPost("/api/ingest/openai/backfill", async (ClaimsPrincipal user, BackfillRequest body,
    OrgAccessService orgs, AppDbContext db, UsageIngestService ingest) =>
{
    var sub = RequireUserSub(user);
    var org = await orgs.GetOrgForUserAsync(sub, body.OrganizationId)
        ?? throw new UnauthorizedAccessException("Organisation ikke fundet");
    await orgs.RequireAdminAsync(sub, org.Id);

    var conn = body.ConnectionId is Guid cid
        ? await db.ProviderConnections.FirstOrDefaultAsync(c => c.Id == cid && c.OrganizationId == org.Id)
        : await db.ProviderConnections.FirstOrDefaultAsync(c =>
            c.OrganizationId == org.Id && c.Provider == ProviderType.OpenAi && c.IsActive);
    if (conn is null) return Results.BadRequest(new { error = "Ingen OpenAI-forbindelse" });

    var to = body.To?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
    var from = body.From?.ToUniversalTime() ?? to.AddDays(-30);
    var (upserted, costs) = await ingest.IngestOpenAiAsync(conn, from, to);
    return Results.Ok(new { upserted, costRows = costs, from, to, connectionId = conn.Id });
}).RequireAuthorization();

app.MapPost("/api/ingest/codex/backfill", async (ClaimsPrincipal user, BackfillRequest body,
    OrgAccessService orgs, AppDbContext db, UsageIngestService ingest) =>
{
    var sub = RequireUserSub(user);
    var org = await orgs.GetOrgForUserAsync(sub, body.OrganizationId)
        ?? throw new UnauthorizedAccessException("Organisation ikke fundet");
    await orgs.RequireAdminAsync(sub, org.Id);

    var conn = body.ConnectionId is Guid cid
        ? await db.ProviderConnections.FirstOrDefaultAsync(c => c.Id == cid && c.OrganizationId == org.Id)
        : await db.ProviderConnections.FirstOrDefaultAsync(c =>
            c.OrganizationId == org.Id && c.Provider == ProviderType.Codex && c.IsActive);
    if (conn is null) return Results.BadRequest(new { error = "Ingen Codex-forbindelse" });

    var (upserted, meters, lifetime) = await ingest.IngestCodexAsync(conn);
    return Results.Ok(new { upserted, meters, lifetimeTokens = lifetime, connectionId = conn.Id });
}).RequireAuthorization();

app.MapGet("/api/usage/summary", async (ClaimsPrincipal user, Guid? organizationId, HttpRequest req,
    OrgAccessService orgs, AppDbContext db) =>
{
    var sub = RequireUserSub(user);
    var org = await orgs.GetOrgForUserAsync(sub, organizationId)
        ?? throw new UnauthorizedAccessException("Organisation ikke fundet");
    var (from, to) = ParseRange(req);

    var q = db.UsageEvents.Where(e => e.OrganizationId == org.Id && e.OccurredAt >= from && e.OccurredAt <= to);
    var summary = new UsageSummaryDto(
        from, to,
        (await q.SumAsync(e => (decimal?)e.CostCents) ?? 0) / 100m,
        (await q.SumAsync(e => (decimal?)e.ChargedCents) ?? 0) / 100m,
        await q.SumAsync(e => (long?)e.InputTokens) ?? 0,
        await q.SumAsync(e => (long?)e.OutputTokens) ?? 0,
        await q.SumAsync(e => (long?)e.CacheWriteTokens) ?? 0,
        await q.SumAsync(e => (long?)e.CacheReadTokens) ?? 0,
        await q.SumAsync(e => (long?)e.RequestCount) ?? 0,
        await q.CountAsync());

    // OpenAI cost reconciliation rows
    var costExtra = (await db.UsageDaily
        .Where(d => d.OrganizationId == org.Id && d.Model == "__cost__"
                    && d.Day >= DateOnly.FromDateTime(from.UtcDateTime)
                    && d.Day <= DateOnly.FromDateTime(to.UtcDateTime))
        .SumAsync(d => (decimal?)d.CostCents) ?? 0) / 100m;

    if (costExtra > 0 && summary.CostUsd == 0)
        summary = summary with { CostUsd = costExtra };

    return Results.Ok(summary);
}).RequireAuthorization();

app.MapGet("/api/usage/series", async (ClaimsPrincipal user, Guid? organizationId, HttpRequest req,
    OrgAccessService orgs, AppDbContext db) =>
{
    var sub = RequireUserSub(user);
    var org = await orgs.GetOrgForUserAsync(sub, organizationId)
        ?? throw new UnauthorizedAccessException("Organisation ikke fundet");
    var (from, to) = ParseRange(req);
    var groupBy = (req.Query["groupBy"].ToString() ?? "model").ToLowerInvariant();
    var fromDay = DateOnly.FromDateTime(from.UtcDateTime);
    var toDay = DateOnly.FromDateTime(to.UtcDateTime);

    var rows = await db.UsageDaily
        .Where(d => d.OrganizationId == org.Id && d.Day >= fromDay && d.Day <= toDay && d.Model != "__cost__")
        .ToListAsync();

    IEnumerable<SeriesPointDto> points = groupBy switch
    {
        "provider" => rows.GroupBy(r => new { r.Day, Key = DtoMaps.ProviderName(r.Provider) })
            .Select(g => new SeriesPointDto(g.Key.Key, g.Key.Day.ToString("yyyy-MM-dd"),
                g.Sum(x => x.InputTokens), g.Sum(x => x.OutputTokens), g.Sum(x => x.CacheReadTokens),
                g.Sum(x => x.CacheWriteTokens), g.Sum(x => x.CostCents) / 100m, g.Sum(x => x.RequestCount))),
        "user" => rows.GroupBy(r => new { r.Day, Key = string.IsNullOrEmpty(r.UserEmail) ? "(ukendt)" : r.UserEmail })
            .Select(g => new SeriesPointDto(g.Key.Key, g.Key.Day.ToString("yyyy-MM-dd"),
                g.Sum(x => x.InputTokens), g.Sum(x => x.OutputTokens), g.Sum(x => x.CacheReadTokens),
                g.Sum(x => x.CacheWriteTokens), g.Sum(x => x.CostCents) / 100m, g.Sum(x => x.RequestCount))),
        _ => rows.GroupBy(r => new { r.Day, Key = string.IsNullOrEmpty(r.Model) ? "(ukendt)" : r.Model })
            .Select(g => new SeriesPointDto(g.Key.Key, g.Key.Day.ToString("yyyy-MM-dd"),
                g.Sum(x => x.InputTokens), g.Sum(x => x.OutputTokens), g.Sum(x => x.CacheReadTokens),
                g.Sum(x => x.CacheWriteTokens), g.Sum(x => x.CostCents) / 100m, g.Sum(x => x.RequestCount)))
    };

    return Results.Ok(points.OrderBy(p => p.Day).ThenBy(p => p.Key).ToList());
}).RequireAuthorization();

app.MapGet("/api/usage/events", async (ClaimsPrincipal user, Guid? organizationId, HttpRequest req,
    OrgAccessService orgs, AppDbContext db) =>
{
    var sub = RequireUserSub(user);
    var org = await orgs.GetOrgForUserAsync(sub, organizationId)
        ?? throw new UnauthorizedAccessException("Organisation ikke fundet");
    var (from, to) = ParseRange(req);
    var page = int.TryParse(req.Query["page"], out var p) && p > 0 ? p : 1;
    var pageSize = int.TryParse(req.Query["pageSize"], out var ps) ? Math.Clamp(ps, 1, 200) : 50;
    var providerFilter = req.Query["provider"].ToString();
    var modelFilter = req.Query["model"].ToString();
    var qFilter = req.Query["q"].ToString();

    var q = db.UsageEvents.AsNoTracking()
        .Where(e => e.OrganizationId == org.Id && e.OccurredAt >= from && e.OccurredAt <= to);

    if (!string.IsNullOrWhiteSpace(providerFilter))
    {
        var prov = DtoMaps.ParseProvider(providerFilter);
        q = q.Where(e => e.Provider == prov);
    }
    if (!string.IsNullOrWhiteSpace(modelFilter))
        q = q.Where(e => e.Model != null && e.Model.Contains(modelFilter));
    if (!string.IsNullOrWhiteSpace(qFilter))
        q = q.Where(e =>
            (e.UserEmail != null && e.UserEmail.Contains(qFilter))
            || (e.Model != null && e.Model.Contains(qFilter))
            || (e.ConversationId != null && e.ConversationId.Contains(qFilter)));

    var total = await q.CountAsync();
    var items = await q.OrderByDescending(e => e.OccurredAt)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .Select(e => new UsageEventDto(
            e.Id, DtoMaps.ProviderName(e.Provider), e.OccurredAt, e.Model, e.UserEmail, e.Kind,
            e.InputTokens, e.OutputTokens, e.CacheWriteTokens, e.CacheReadTokens,
            e.RequestCount, e.CostCents, e.ChargedCents, e.ConversationId))
        .ToListAsync();

    return Results.Ok(new PagedEventsDto(items, total, page, pageSize));
}).RequireAuthorization();

app.MapGet("/api/meters", async (ClaimsPrincipal user, Guid? organizationId,
    OrgAccessService orgs, AppDbContext db) =>
{
    var sub = RequireUserSub(user);
    var org = await orgs.GetOrgForUserAsync(sub, organizationId)
        ?? throw new UnauthorizedAccessException("Organisation ikke fundet");
    var meters = await db.LiveMeters
        .Where(m => m.OrganizationId == org.Id)
        .OrderBy(m => m.Provider).ThenBy(m => m.Label)
        .Select(m => new LiveMeterDto(DtoMaps.ProviderName(m.Provider), m.Label, m.UsedPercent, m.ResetsAt, m.DisplayValue, m.MeasuredAt))
        .ToListAsync();
    return Results.Ok(meters);
}).RequireAuthorization();

app.Run();
