namespace Orchestrator.Domain.Memory;

/// <summary>
/// A decision or durable observation proposed for memory curation: the note content plus
/// the originating task, the verbatim evidence passages, and when it was proposed.
/// Curation (Task 42) fills in the vault location and revision once published.
/// </summary>
public sealed record MemoryProposal(
    Guid Id,
    Guid? TaskId,
    string Content,
    IReadOnlyList<string> Evidence,
    DateTimeOffset CreatedAt,
    Guid? SourceTaskId = null,
    string? VaultPath = null,
    string? VaultRevisionId = null)
{
    private MemoryProposal()
        : this(Guid.Empty, null, string.Empty, [], default)
    {
    }
}
