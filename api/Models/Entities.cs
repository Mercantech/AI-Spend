namespace AiSpend.Api.Models;

public enum MembershipRole
{
    Owner = 0,
    Admin = 1,
    Member = 2
}

public enum ProviderType
{
    Cursor = 0,
    OpenAi = 1,
    Codex = 2
}

public enum ConnectionScope
{
    Personal = 0,
    Organization = 1
}

public class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Membership> Memberships { get; set; } = [];
    public List<ProviderConnection> Connections { get; set; } = [];
    public List<UsageEvent> UsageEvents { get; set; } = [];
    public List<UsageDaily> UsageDaily { get; set; } = [];
    public List<LiveMeter> LiveMeters { get; set; } = [];
    public List<Budget> Budgets { get; set; } = [];
}

public class Membership
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public Guid UserSub { get; set; }
    public MembershipRole Role { get; set; } = MembershipRole.Member;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ProviderConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public ProviderType Provider { get; set; }
    public ConnectionScope Scope { get; set; } = ConnectionScope.Personal;
    public string Label { get; set; } = "";
    /// <summary>Encrypted credential blob (API key / token).</summary>
    public string? EncryptedCredential { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastSyncAt { get; set; }
    public string? LastSyncError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class UsageEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public ProviderType Provider { get; set; }
    public string ExternalKey { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public string? Model { get; set; }
    public string? UserEmail { get; set; }
    public string? Kind { get; set; }
    public string? ConversationId { get; set; }
    public bool? IsTokenBasedCall { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long RequestCount { get; set; }
    public decimal CostCents { get; set; }
    public decimal ChargedCents { get; set; }
    public decimal CursorTokenFee { get; set; }
    public decimal RequestsCosts { get; set; }
    public Guid? RawPayloadId { get; set; }
    public ProviderRaw? RawPayload { get; set; }
    public DateTimeOffset IngestedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class UsageDaily
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public DateOnly Day { get; set; }
    public ProviderType Provider { get; set; }
    public string Model { get; set; } = "";
    public string UserEmail { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long RequestCount { get; set; }
    public decimal CostCents { get; set; }
    public decimal ChargedCents { get; set; }
}

public class LiveMeter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public ProviderType Provider { get; set; }
    public string Label { get; set; } = "";
    public double UsedPercent { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }
    public string? DisplayValue { get; set; }
    public DateTimeOffset MeasuredAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ProviderRaw
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public ProviderType Provider { get; set; }
    public string PayloadJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Budget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;
    public ProviderType? Provider { get; set; }
    public decimal MonthlyLimitUsd { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
