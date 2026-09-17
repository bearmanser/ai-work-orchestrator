using Orchestrator.Application.Ports;
using Orchestrator.Domain.Evidence;

namespace Orchestrator.Application.Evidence;

/// <summary>Reads the evidence pieces stored for one task. Implemented with EF later; tests use fakes.</summary>
public interface ITaskEvidenceStore
{
    Task<IReadOnlyList<CheckResult>> GetRuntimeChecksAsync(Guid taskId, CancellationToken ct);
    Task<IReadOnlyList<DomainCheckResult>> GetDomainChecksAsync(Guid taskId, CancellationToken ct);
    Task<GitDiffSummary> GetDiffSummaryAsync(Guid taskId, CancellationToken ct);
    Task<string> GetPlanRevisionHashAsync(Guid taskId, CancellationToken ct);
    Task<IReadOnlyList<string>> GetUnresolvedQuestionsAsync(Guid taskId, CancellationToken ct);
}

/// <summary>Composes the stored evidence pieces into one bundle for PR descriptions and review.</summary>
public sealed class EvidenceAssembler(ITaskEvidenceStore store)
{
    public async Task<TaskEvidence> AssembleAsync(Guid taskId, CancellationToken ct) => new(
        await store.GetRuntimeChecksAsync(taskId, ct),
        await store.GetDomainChecksAsync(taskId, ct),
        await store.GetDiffSummaryAsync(taskId, ct),
        await store.GetPlanRevisionHashAsync(taskId, ct),
        await store.GetUnresolvedQuestionsAsync(taskId, ct));
}
