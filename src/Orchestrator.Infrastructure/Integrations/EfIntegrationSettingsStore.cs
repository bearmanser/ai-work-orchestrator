using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Integrations;
using Orchestrator.Domain.Integrations;
using Orchestrator.Infrastructure.Persistence;

namespace Orchestrator.Infrastructure.Integrations;

/// <summary>EF-backed <see cref="IIntegrationSettingsStore"/>, one row per named integration.</summary>
public sealed class EfIntegrationSettingsStore(OrchestratorDbContext db, TimeProvider clock) : IIntegrationSettingsStore
{
    public async Task<IntegrationSettingRecord?> GetAsync(string name, CancellationToken ct)
    {
        var row = await db.IntegrationSettings.SingleOrDefaultAsync(r => r.Name == name, ct);
        return row is null
            ? null
            : new IntegrationSettingRecord(row.Name, row.SecretRef, row.LastCheckedAt, row.Health, row.Detail);
    }

    public async Task SetSecretRefAsync(string name, string secretRef, DateTimeOffset at, CancellationToken ct)
    {
        var row = await GetOrCreateAsync(name, ct);
        row.SetSecretRef(secretRef, at);
        await db.SaveChangesAsync(ct);
    }

    public async Task RecordTestResultAsync(string name, string health, string? detail, DateTimeOffset at, CancellationToken ct)
    {
        var row = await GetOrCreateAsync(name, ct);
        row.RecordTestResult(health, detail, at);
        await db.SaveChangesAsync(ct);
    }

    private async Task<IntegrationSetting> GetOrCreateAsync(string name, CancellationToken ct)
    {
        var row = await db.IntegrationSettings.SingleOrDefaultAsync(r => r.Name == name, ct);
        if (row is not null)
            return row;

        row = new IntegrationSetting(name, clock.GetUtcNow());
        db.IntegrationSettings.Add(row);
        return row;
    }
}
