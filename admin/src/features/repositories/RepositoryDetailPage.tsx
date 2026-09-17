import { BuildCommandsForm } from "./BuildCommandsForm";
import { RelationshipGraph } from "./RelationshipGraph";
import { useDeleteRelationship, useRelationships, useRepositoryDetail } from "./useRepositories";

export interface RepositoryDetailPageProps {
  repositoryId: string;
}

export function RepositoryDetailPage({ repositoryId }: RepositoryDetailPageProps) {
  const { data: repository, isLoading, isError } = useRepositoryDetail(repositoryId);
  const { data: relationships } = useRelationships(repositoryId);
  const deleteRelationship = useDeleteRelationship(repositoryId);

  if (isLoading) {
    return <p>Loading repository…</p>;
  }
  if (isError || repository === undefined) {
    return <p role="alert">This repository could not be loaded.</p>;
  }

  return (
    <main>
      <h1>{repository.name}</h1>
      <dl>
        <dt>Remote</dt>
        <dd>{repository.remoteUrl}</dd>

        <dt>Default branch</dt>
        <dd>{repository.defaultBranch}</dd>

        <dt>Purpose</dt>
        <dd>{repository.purpose}</dd>

        <dt>Owner</dt>
        <dd>{repository.ownerContactId}</dd>

        <dt>Access policy</dt>
        <dd>{repository.accessPolicy}</dd>

        <dt>Last inspected</dt>
        <dd>
          {repository.lastInspectedAt === null
            ? "Never inspected"
            : `${new Date(repository.lastInspectedAt).toLocaleString()} at commit ${repository.lastInspectedCommit ?? "unknown"}`}
        </dd>
      </dl>

      <section aria-label="Build commands">
        <h2>Build commands</h2>
        <BuildCommandsForm repositoryId={repositoryId} />
      </section>

      <section aria-label="Relationships">
        <h2>Relationships</h2>
        {relationships !== undefined && (
          <RelationshipGraph
            relationships={relationships}
            onRemove={(relationshipId) => deleteRelationship.mutate(relationshipId)}
            isRemoving={deleteRelationship.isPending}
          />
        )}
        {deleteRelationship.isError && (
          <p role="alert">Could not remove relationship: {deleteRelationship.error.message}</p>
        )}
      </section>
    </main>
  );
}
