namespace Orchestrator.Domain.Operations;

/// <summary>
/// The persisted state of one idempotent tool call. Intent is recorded before
/// invoking; a crash between intent and outcome leaves <see cref="Unknown"/>.
/// </summary>
public enum ToolCallOutcome
{
    Unknown,
    Succeeded,
}