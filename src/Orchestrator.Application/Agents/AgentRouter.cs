using System.Text;
using Orchestrator.Application.Ports;
using Orchestrator.Domain.Evidence;

namespace Orchestrator.Application.Agents;

/// <summary>
/// Selects a provider by the request's <see cref="ProviderPolicy"/>, retries
/// transient failures with backoff, and falls back only when permitted and only
/// for classes where another provider can plausibly succeed. A denial or an
/// implementation failure never falls back: switching models must not defeat
/// policy, and a different model is not the remedy for a change that did not pass.
/// </summary>
public sealed class AgentRouter(
    IEnumerable<ICodingAgent> providers,
    IGitWorkspace workspaces,
    IArtifactStore artifacts,
    Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
{
    private static readonly TimeSpan[] Backoffs = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)];
    private const int MaxAttemptsPerProvider = 3;

    public async Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        var available = providers.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var ordered = request.ProviderPolicy.AllowedProviders
            .Where(name => available.ContainsKey(name))
            .Select(name => available[name])
            .ToList();

        if (ordered.Count == 0)
        {
            return Failed(request, string.Empty,
                $"No configured provider is in the allowed list [{string.Join(", ", request.ProviderPolicy.AllowedProviders)}].");
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var (result, abandonDetail) = await AttemptAsync(ordered[i], request, ct);
            if (result is not null)
                return result;

            if (!request.ProviderPolicy.FallbackAllowed || i == ordered.Count - 1)
                return Failed(request, ordered[i].Name, abandonDetail ?? "Agent execution was abandoned.");

            await CapturePartialDiffAsync(request, ct);
        }

        throw new InvalidOperationException("Unreachable: the provider list is non-empty and every iteration returns.");
    }

    private async Task<(AgentResult? Result, string? AbandonDetail)> AttemptAsync(
        ICodingAgent provider, AgentRequest request, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await provider.ExecuteAsync(request, ct);
                // A returned result is final for this provider: success returns,
                // and an implementation failure never retries and never falls back.
                return (result with { Provider = provider.Name }, null);
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                    throw;

                var kind = FailureClassifier.Classify(ex, null);
                if (kind is FailureClass.PolicyDenial)
                    return (Failed(request, provider.Name, ex.Message), null);
                if (kind is not (FailureClass.Transient or FailureClass.Authentication or FailureClass.UnsupportedCapability))
                    return (Failed(request, provider.Name, ex.Message), null);
                if (kind is FailureClass.Transient && attempt < MaxAttemptsPerProvider)
                {
                    await DelayAsync(Backoffs[attempt - 1], ct);
                    continue;
                }

                return (null, ex.Message);
            }
        }
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken ct) =>
        delayAsync is null ? Task.Delay(delay, ct) : delayAsync(delay, ct);

    /// <summary>
    /// Best effort: preserving partial work must not fail the fallback itself.
    /// </summary>
    private async Task CapturePartialDiffAsync(AgentRequest request, CancellationToken ct)
    {
        try
        {
            var diff = await workspaces.CaptureDiffAsync(request.Workspace.WorkspaceId, ct);
            await artifacts.PutAsync(
                new ArtifactDescriptor(
                    request.TaskId, "agent-partial-diff", "text/plain",
                    ArtifactSensitivity.Internal, "agent-router"),
                new MemoryStream(Encoding.UTF8.GetBytes(diff.UnifiedDiff)), ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The next provider still runs; reconciliation (Task 23) owns the workspace truth.
        }
    }

    private static AgentResult Failed(AgentRequest request, string provider, string detail) => new(
        AgentStatus.Failed, provider, string.Empty, request.Workspace.BaselineCommit,
        null, null, [], [], [], new AgentUsage(0m, TimeSpan.Zero), detail);
}
