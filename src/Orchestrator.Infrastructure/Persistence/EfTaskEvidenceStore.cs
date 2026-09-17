using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Application.Evidence;
using Orchestrator.Application.Humans;
using Orchestrator.Application.Plans;
using Orchestrator.Application.Ports;
using Orchestrator.Application.Steps;
using Orchestrator.Domain.Evidence;
using Orchestrator.Domain.Plans;

namespace Orchestrator.Infrastructure.Persistence;

/// <summary>
/// Assembles evidence from where each piece durably lives: check runs ride the
/// verification step's output artifact, the plan hash rides the confirmed
/// revision, and open questions ride the question store. No new tables.
/// </summary>
public sealed class EfTaskEvidenceStore(
    OrchestratorDbContext db,
    IArtifactStore artifacts,
    IPlanConfirmationStore confirmations,
    IHumanQuestionStore questions) : ITaskEvidenceStore
{
    public async Task<IReadOnlyList<CheckResult>> GetRuntimeChecksAsync(Guid taskId, CancellationToken ct) =>
        (await LoadEvidenceAsync(taskId, ct))?.RuntimeChecks ?? [];

    public async Task<IReadOnlyList<DomainCheckResult>> GetDomainChecksAsync(Guid taskId, CancellationToken ct) =>
        (await LoadEvidenceAsync(taskId, ct))?.DomainChecks ?? [];

    public async Task<GitDiffSummary> GetDiffSummaryAsync(Guid taskId, CancellationToken ct) =>
        (await LoadEvidenceAsync(taskId, ct))?.Diff
            ?? new GitDiffSummary(string.Empty, string.Empty, [], null);

    public async Task<string> GetPlanRevisionHashAsync(Guid taskId, CancellationToken ct)
    {
        var revisions = await confirmations.ListRevisionsAsync(taskId, ct);
        return revisions
            .Where(r => r.ConfirmationStatus == ConfirmationStatus.Confirmed)
            .OrderByDescending(r => r.RevisionNumber)
            .FirstOrDefault()?.ContentHash ?? string.Empty;
    }

    public async Task<IReadOnlyList<string>> GetUnresolvedQuestionsAsync(Guid taskId, CancellationToken ct)
    {
        var open = await questions.ListOpenAsync(ct);
        return open
            .Where(q => q.TaskId == taskId)
            .Select(q => q.QuestionText)
            .ToList();
    }

    private async Task<TaskEvidence?> LoadEvidenceAsync(Guid taskId, CancellationToken ct)
    {
        var step = await db.Steps
            .Where(s => s.TaskId == taskId && s.Capability == "verification" && s.OutputArtifactId != null)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (step?.OutputArtifactId is null)
            return null;

        await using var stream = await artifacts.OpenAsync(step.OutputArtifactId, ct);
        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync(ct);
        try
        {
            return JsonSerializer.Deserialize<TaskEvidence>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
