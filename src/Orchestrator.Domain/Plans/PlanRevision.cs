using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orchestrator.Domain.Plans;

public enum ConfirmationStatus
{
    Pending,
    Confirmed,
    ChangesRequested,
    Superseded,
}

/// <summary>
/// One revision of a task's plan. Confirmation binds to the exact
/// <see cref="ContentHash"/>: any material change is a new revision that needs
/// its own confirmation.
/// </summary>
public sealed class PlanRevision
{
    private PlanRevision() { }

    public PlanRevision(
        Guid taskId,
        int revisionNumber,
        PlanContent content,
        Guid? contextSnapshotId = null)
    {
        Id = Guid.CreateVersion7();
        TaskId = taskId;
        RevisionNumber = revisionNumber;
        Content = content;
        ContentHash = ComputeHash(content);
        ConfirmationStatus = ConfirmationStatus.Pending;
        ContextSnapshotId = contextSnapshotId;
    }

    public Guid Id { get; private set; }
    public Guid TaskId { get; private set; }
    public int RevisionNumber { get; private set; }

    /// <summary>
    /// The persisted form of <see cref="Content"/>: canonical JSON in a jsonb
    /// column. EF owns this string; readers go through <see cref="Content"/>.
    /// </summary>
    public string ContentJson { get; private set; } = null!;

    private PlanContent? _content;

    /// <summary>
    /// The plan content, hydrated from <see cref="ContentJson"/> on first access
    /// after EF materialization.
    /// </summary>
    public PlanContent Content
    {
        get => _content ??= JsonSerializer.Deserialize<PlanContent>(ContentJson)
            ?? throw new InvalidOperationException("Stored plan content is not valid JSON.");
        private set
        {
            _content = value;
            ContentJson = JsonSerializer.Serialize(value);
        }
    }

    public string ContentHash { get; private set; } = null!;
    public ConfirmationStatus ConfirmationStatus { get; private set; }
    public Guid? ConfirmedByPersonId { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    public Guid? SupersededByRevisionId { get; private set; }
    public Guid? ContextSnapshotId { get; private set; }

    public void Confirm(Guid personId, DateTimeOffset now)
    {
        if (ConfirmationStatus != ConfirmationStatus.Pending)
            throw new InvalidOperationException($"Plan revision is {ConfirmationStatus}, not Pending.");
        ConfirmationStatus = ConfirmationStatus.Confirmed;
        ConfirmedByPersonId = personId;
        ConfirmedAt = now.ToUniversalTime();
    }

    public void RequestChanges()
    {
        if (ConfirmationStatus != ConfirmationStatus.Pending)
            throw new InvalidOperationException($"Plan revision is {ConfirmationStatus}, not Pending.");
        ConfirmationStatus = ConfirmationStatus.ChangesRequested;
    }

    public void Supersede(Guid newRevisionId)
    {
        SupersededByRevisionId = newRevisionId;
        if (ConfirmationStatus == ConfirmationStatus.Pending)
            ConfirmationStatus = ConfirmationStatus.Superseded;
    }

    /// <summary>
    /// SHA-256 over canonical JSON that preserves list order and includes every
    /// repository change baseline, so any material change — including order —
    /// produces a different hash.
    /// </summary>
    public static string ComputeHash(PlanContent content)
    {
        var canonical = new
        {
            content.Objective,
            Scope = content.Scope.ToArray(),
            Exclusions = content.Exclusions.ToArray(),
            content.Approach,
            RepositoryChanges = content.RepositoryChanges
                .Select(r => new
                {
                    r.RepositoryId,
                    r.RepositoryName,
                    Intent = r.Intent.ToString(),
                    r.BaselineCommit,
                })
                .ToArray(),
            Tests = content.Tests.ToArray(),
            Risks = content.Risks.ToArray(),
            OpenQuestions = content.OpenQuestions.ToArray(),
            Providers = content.Providers.ToArray(),
            P1Checks = content.P1Checks.ToArray(),
            Budget = new
            {
                content.Budget.MaxUsd,
                MaxWallClockTicks = content.Budget.MaxWallClock.Ticks,
                content.Budget.MaxAgentInvocations,
                content.Budget.MaxCorrectionAttempts,
            },
        };

        var json = JsonSerializer.Serialize(canonical);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
