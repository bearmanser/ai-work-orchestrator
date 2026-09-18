import { useState } from "react";
import { Link } from "react-router-dom";
import { RepositoryAccess } from "../../api/types";
import { useCreateRepository, useRepositoryList } from "./useRepositories";

/** Lists the repository catalog and lets an admin add a new repository. */
export function RepositoryListPage() {
  const { data, isLoading, isError } = useRepositoryList();
  const createRepository = useCreateRepository();

  const [name, setName] = useState("");
  const [remoteUrl, setRemoteUrl] = useState("");
  const [defaultBranch, setDefaultBranch] = useState("main");
  const [purpose, setPurpose] = useState("");
  const [ownerContactId, setOwnerContactId] = useState("");

  function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    createRepository.mutate(
      {
        name,
        remoteUrl,
        defaultBranch,
        purpose,
        ownerContactId,
        accessPolicy: RepositoryAccess.Write,
        buildCommands: {
          restore: null,
          build: null,
          test: null,
          lint: null,
          workingDirectory: ".",
          timeout: "00:30:00",
        },
        instructionFilePaths: [],
      },
      {
        onSuccess: () => {
          setName("");
          setRemoteUrl("");
          setPurpose("");
          setOwnerContactId("");
        },
      },
    );
  }

  return (
    <main>
      <h1>Repositories</h1>

      {isLoading && <p>Loading repositories…</p>}
      {isError && <p role="alert">Repositories could not be loaded.</p>}

      {data !== undefined && (
        <>
          {data.length === 0 ? (
            <p>No repositories are in the catalog yet.</p>
          ) : (
            <table>
              <thead>
                <tr>
                  <th scope="col">Name</th>
                  <th scope="col">Purpose</th>
                  <th scope="col">Default branch</th>
                  <th scope="col">Last inspected</th>
                </tr>
              </thead>
              <tbody>
                {data.map((repository) => (
                  <tr key={repository.id}>
                    <td>
                      <Link to={`/repositories/${repository.id}`}>{repository.name}</Link>
                    </td>
                    <td>{repository.purpose}</td>
                    <td>{repository.defaultBranch}</td>
                    <td>{repository.lastInspectedAt === null ? "Never" : new Date(repository.lastInspectedAt).toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </>
      )}

      <section aria-label="Add repository">
        <h2>Add a repository</h2>
        <form onSubmit={handleSubmit}>
          {createRepository.isError && (
            <p role="alert">Could not add repository: {createRepository.error.message}</p>
          )}

          <label htmlFor="repo-name">Name</label>
          <input id="repo-name" required value={name} onChange={(event) => setName(event.target.value)} />

          <label htmlFor="repo-remote">Remote URL</label>
          <input
            id="repo-remote"
            required
            value={remoteUrl}
            onChange={(event) => setRemoteUrl(event.target.value)}
          />

          <label htmlFor="repo-branch">Default branch</label>
          <input
            id="repo-branch"
            required
            value={defaultBranch}
            onChange={(event) => setDefaultBranch(event.target.value)}
          />

          <label htmlFor="repo-purpose">Purpose</label>
          <input id="repo-purpose" required value={purpose} onChange={(event) => setPurpose(event.target.value)} />

          <label htmlFor="repo-owner">Owner contact</label>
          <input
            id="repo-owner"
            required
            value={ownerContactId}
            onChange={(event) => setOwnerContactId(event.target.value)}
          />

          <button type="submit" disabled={createRepository.isPending}>
            Add repository
          </button>
        </form>
      </section>
    </main>
  );
}
