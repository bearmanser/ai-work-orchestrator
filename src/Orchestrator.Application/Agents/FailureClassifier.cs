using System.Net;
using Orchestrator.Application.Humans;
using Orchestrator.Application.Ports;

namespace Orchestrator.Application.Agents;

public enum FailureClass
{
    None,
    Transient,
    Authentication,
    UnsupportedCapability,
    PolicyDenial,
    ImplementationFailure,
}

/// <summary>
/// Maps a provider exception or result to a failure class. The class — not the
/// provider that produced it — decides whether the router retries or falls back.
/// </summary>
public static class FailureClassifier
{
    public static FailureClass Classify(Exception? exception, AgentResult? result)
    {
        if (exception is null)
        {
            return result?.Status == AgentStatus.Failed
                ? FailureClass.ImplementationFailure
                : FailureClass.None;
        }

        return exception switch
        {
            PolicyDeniedException => FailureClass.PolicyDenial,
            HttpRequestException ex when ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                => FailureClass.Authentication,
            HttpRequestException => FailureClass.Transient,
            TaskCanceledException => FailureClass.Transient,
            NotSupportedException => FailureClass.UnsupportedCapability,
            // Unknown failures are treated as transient: retries are bounded and
            // a persistent one still surfaces as a failure, while treating them
            // as fatal could strand a task on a fleeting infrastructure fault.
            _ => FailureClass.Transient,
        };
    }
}
