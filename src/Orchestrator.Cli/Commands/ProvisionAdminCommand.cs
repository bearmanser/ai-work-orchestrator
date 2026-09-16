using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Identity;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Identity;
using Orchestrator.Infrastructure.Audit;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Cli.Commands;

/// <summary>
/// Creates one admin account. This is the only way an account is created - there is
/// no self-service registration endpoint.
/// </summary>
public sealed class ProvisionAdminCommand(
    OrchestratorDbContext db, IPasswordHasher hasher, TimeProvider clock)
{
    public const int MinimumPasswordLength = 12;

    public async Task ExecuteAsync(string login, string password, CancellationToken ct)
    {
        if (password.Length < MinimumPasswordLength)
            throw new ArgumentException(
                $"Password must be at least {MinimumPasswordLength} characters.", nameof(password));

        var loginName = login.Trim().ToLowerInvariant();

        var exists = await db.AdminAccounts.AnyAsync(a => a.LoginName == loginName, ct);
        if (exists)
            throw new InvalidOperationException($"An admin account with login '{loginName}' already exists.");

        var now = clock.GetUtcNow();
        var account = new AdminAccount(loginName, hasher.Hash(password), now);
        db.AdminAccounts.Add(account);
        await db.SaveChangesAsync(ct);

        var auditWriter = new AuditWriter(db, clock);
        await auditWriter.RecordAsync(new AuditEntry(
            Actor: "cli:provision-admin",
            TaskId: null,
            Action: "admin.provisioned",
            Reason: $"Admin account '{loginName}' provisioned via CLI.",
            Outcome: "success",
            CorrelationId: account.Id.ToString(),
            Details: new Dictionary<string, string> { ["loginName"] = loginName }), ct);
    }
}
