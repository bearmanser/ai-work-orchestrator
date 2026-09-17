namespace Orchestrator.Application.Context;

public sealed record ContextBudget(int MaxCharacters, int MaxFilesPerRepository, int MaxVaultNotes);
