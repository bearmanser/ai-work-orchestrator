using Orchestrator.Domain.Authorization;
using Orchestrator.Domain.Budgets;
using Orchestrator.Domain.Tasks;

namespace Orchestrator.Application.Plans;

/// <summary>Task, envelope, and ledger reads for the planning step. Implemented in Infrastructure.</summary>
public interface IPlanningScopeStore
{
    Task<WorkTask> GetTaskAsync(Guid taskId, CancellationToken ct);
    Task SaveTaskAsync(WorkTask task, CancellationToken ct);
    Task<AuthorizationEnvelope?> GetEnvelopeAsync(Guid taskId, CancellationToken ct);
    Task<BudgetLedger?> GetLedgerAsync(Guid taskId, CancellationToken ct);
}
