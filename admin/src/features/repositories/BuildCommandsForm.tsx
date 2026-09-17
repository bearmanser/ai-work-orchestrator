import { useEffect, useState } from "react";
import { useRepositoryDetail, useUpdateRepository } from "./useRepositories";

export interface BuildCommandsFormProps {
  repositoryId: string;
}

/**
 * Edits a repository's build/test/lint commands (Orchestrator.Domain.Repositories.BuildCommands).
 * These are executed by the orchestrator runtime, never the agent, so a wrong
 * or missing test command silently means "tests never ran" - the form
 * validates that up front rather than letting it ship quietly.
 */
export function BuildCommandsForm({ repositoryId }: BuildCommandsFormProps) {
  const { data: repository, isLoading, isError } = useRepositoryDetail(repositoryId);
  const updateRepository = useUpdateRepository(repositoryId);

  const [restore, setRestore] = useState("");
  const [build, setBuild] = useState("");
  const [test, setTest] = useState("");
  const [lint, setLint] = useState("");
  const [workingDirectory, setWorkingDirectory] = useState("");
  const [validationError, setValidationError] = useState<string | null>(null);

  useEffect(() => {
    if (repository === undefined) return;
    setRestore(repository.buildCommands.restore ?? "");
    setBuild(repository.buildCommands.build ?? "");
    setTest(repository.buildCommands.test ?? "");
    setLint(repository.buildCommands.lint ?? "");
    setWorkingDirectory(repository.buildCommands.workingDirectory);
  }, [repository]);

  if (isLoading) {
    return <p>Loading build commands…</p>;
  }
  if (isError || repository === undefined) {
    return <p role="alert">This repository's build commands could not be loaded.</p>;
  }

  function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    if (repository === undefined) return;

    // "Tests meaningfully enabled" is inferred here as: a build step is
    // configured, so this repository is expected to be built and verified.
    // If that is true, a blank test command means checks silently never run.
    if (build.trim().length > 0 && test.trim().length === 0) {
      setValidationError("Test command is required when a build command is configured.");
      return;
    }
    setValidationError(null);

    updateRepository.mutate({
      request: {
        name: repository.name,
        purpose: repository.purpose,
        defaultBranch: repository.defaultBranch,
        ownerContactId: repository.ownerContactId,
        buildCommands: {
          restore: restore.trim().length > 0 ? restore : null,
          build: build.trim().length > 0 ? build : null,
          test: test.trim().length > 0 ? test : null,
          lint: lint.trim().length > 0 ? lint : null,
          workingDirectory,
          timeout: repository.buildCommands.timeout,
        },
        instructionFilePaths: repository.instructionFilePaths,
      },
      expectedVersion: repository.version,
    });
  }

  return (
    <form onSubmit={handleSubmit} aria-label="Build commands">
      {validationError !== null && <p role="alert">{validationError}</p>}
      {updateRepository.isError && (
        <p role="alert">Could not save build commands: {updateRepository.error.message}</p>
      )}
      {updateRepository.isSuccess && <p>Build commands saved.</p>}

      <label htmlFor="restore-command">Restore command</label>
      <input id="restore-command" value={restore} onChange={(event) => setRestore(event.target.value)} />

      <label htmlFor="build-command">Build command</label>
      <input id="build-command" value={build} onChange={(event) => setBuild(event.target.value)} />

      <label htmlFor="test-command">Test command</label>
      <input id="test-command" value={test} onChange={(event) => setTest(event.target.value)} />

      <label htmlFor="lint-command">Lint command</label>
      <input id="lint-command" value={lint} onChange={(event) => setLint(event.target.value)} />

      <label htmlFor="working-directory">Working directory</label>
      <input
        id="working-directory"
        value={workingDirectory}
        onChange={(event) => setWorkingDirectory(event.target.value)}
      />

      <button type="submit" disabled={updateRepository.isPending}>
        Save
      </button>
    </form>
  );
}
