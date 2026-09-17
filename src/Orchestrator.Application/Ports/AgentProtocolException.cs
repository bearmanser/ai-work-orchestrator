namespace Orchestrator.Application.Ports;

/// <summary>
/// Thrown when a coding agent CLI's output cannot be parsed into the envelope its provider
/// expects. This is distinct from <see cref="AgentStatus.Failed"/>: a <c>Failed</c> status means
/// the agent ran and reported it did not succeed; this exception means the provider could not
/// even understand what the agent said, so the caller (the provider router) must treat the two
/// differently - a protocol failure may warrant falling back to another provider, an
/// implementation failure should not.
/// </summary>
public sealed class AgentProtocolException : Exception
{
    public AgentProtocolException(string message, string? rawOutput = null)
        : base(message)
    {
        RawOutput = rawOutput;
    }

    /// <summary>The unparseable output that triggered this exception, for diagnostics.</summary>
    public string? RawOutput { get; }
}
