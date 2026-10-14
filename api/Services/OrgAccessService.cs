using AiSpend.Api.Data;
using AiSpend.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AiSpend.Api.Services;

public sealed class OrgAccessService(AppDbContext db)
{
    public async Task<Organization> EnsurePersonalOrgAsync(Guid userSub, CancellationToken ct = default)
    {
        var existing = await db.Memberships
            .Include(m => m.Organization)
            .Where(m => m.UserSub == userSub && m.Role == MembershipRole.Owner)
            .Select(m => m.Organization)
            .FirstOrDefaultAsync(ct);

        if (existing is not null) return existing;

        var org = new Organization { Name = "Personlig" };
        db.Organizations.Add(org);
        db.Memberships.Add(new Membership
        {
            OrganizationId = org.Id,
            UserSub = userSub,
            Role = MembershipRole.Owner
        });
        await db.SaveChangesAsync(ct);
        return org;
    }

    public async Task<Organization?> GetOrgForUserAsync(Guid userSub, Guid? orgId, CancellationToken ct = default)
    {
        if (orgId is null)
            return await EnsurePersonalOrgAsync(userSub, ct);

        var member = await db.Memberships
            .Include(m => m.Organization)
            .FirstOrDefaultAsync(m => m.UserSub == userSub && m.OrganizationId == orgId, ct);
        return member?.Organization;
    }

    public async Task RequireMemberAsync(Guid userSub, Guid orgId, CancellationToken ct = default)
    {
        var ok = await db.Memberships.AnyAsync(m => m.UserSub == userSub && m.OrganizationId == orgId, ct);
        if (!ok) throw new UnauthorizedAccessException("Ikke medlem af organisationen");
    }

    public async Task RequireAdminAsync(Guid userSub, Guid orgId, CancellationToken ct = default)
    {
        var role = await db.Memberships
            .Where(m => m.UserSub == userSub && m.OrganizationId == orgId)
            .Select(m => (MembershipRole?)m.Role)
            .FirstOrDefaultAsync(ct);
        if (role is null || role > MembershipRole.Admin)
            throw new UnauthorizedAccessException("Kræver admin/owner");
    }
}
