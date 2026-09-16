namespace Orchestrator.Domain.Repositories;

/// <summary>One file proving (part of) a relationship.</summary>
public sealed record RelationshipEvidence(string SourcePath, string CommitSha, string Excerpt);
