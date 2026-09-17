using Orchestrator.Domain.Evidence;

namespace Orchestrator.Application.Ports;

/// <summary>Executes allowlisted, read-only named queries against a domain data source.</summary>
public interface IDomainDataSource
{
    Task<DomainCheckResult> ExecuteNamedQueryAsync(DomainQueryRequest request, CancellationToken ct);
}
