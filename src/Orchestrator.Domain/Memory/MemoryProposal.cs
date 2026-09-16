namespace Orchestrator.Domain.Memory;

/// <summary>
/// A decision or durable observation proposed for memory curation. Minimal
/// record for now: the note content plus the originating task, the verbatim
/// evidence passages, and when it was proposed. Task 42 builds curation on it.
/// </summary>
public sealed record MemoryProposal(
    Guid Id,
    Guid? TaskId,
    string Content,
    IReadOnlyList<string> Evidence,
    DateTimeOffset CreatedAt);
