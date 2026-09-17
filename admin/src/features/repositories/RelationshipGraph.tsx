import type { RelationshipStatus, RepositoryRelationship } from "../../api/types";

export interface RelationshipGraphProps {
  relationships: RepositoryRelationship[];
  onRemove: (relationshipId: string) => void;
  isRemoving?: boolean;
}

/**
 * Human-readable label for a relationship status. Rendered as text (not just
 * colour) everywhere so the distinction between evidenced, admin-asserted,
 * and conflicted survives a screenshot and is readable by a screen reader.
 */
function statusLabel(status: RelationshipStatus): string {
  switch (status) {
    case "Evidenced":
      return "Evidenced";
    case "AdminAsserted":
      return "Asserted by an administrator";
    case "Conflicted":
      return "Conflicted";
    default:
      return status;
  }
}

/**
 * Reviews every relationship a repository has to others, with its evidence.
 * Automation-derived (`Evidenced`) relationships can never be deleted here -
 * the server enforces that too, but the control is disabled up front rather
 * than letting the admin click into a 409.
 */
export function RelationshipGraph({ relationships, onRemove, isRemoving }: RelationshipGraphProps) {
  if (relationships.length === 0) {
    return <p>No relationships have been recorded for this repository.</p>;
  }

  return (
    <ul>
      {relationships.map((relationship) => {
        const isEvidenced = relationship.status === "Evidenced";
        return (
          <li key={relationship.id}>
            <p>
              <strong>{relationship.relationshipType}</strong> → {relationship.targetRepositoryId}
              {" — "}
              <span>{statusLabel(relationship.status)}</span>
            </p>

            {relationship.status === "Conflicted" && (
              <p role="alert">
                Conflict: {relationship.adminNote ?? "This relationship's evidence disagrees with a prior assertion."}
              </p>
            )}

            {relationship.evidence.length === 0 ? (
              <p>No evidence recorded.</p>
            ) : (
              <ul aria-label={`Evidence for ${relationship.relationshipType} relationship`}>
                {relationship.evidence.map((evidence, index) => (
                  <li key={`${relationship.id}-${index}`}>
                    <code>{evidence.sourcePath}</code> at commit <code>{evidence.commitSha}</code>
                  </li>
                ))}
              </ul>
            )}

            <button
              type="button"
              disabled={isEvidenced || isRemoving}
              title={isEvidenced ? "Evidenced relationships cannot be removed; correct the underlying evidence instead." : undefined}
              onClick={() => onRemove(relationship.id)}
            >
              Remove
            </button>
          </li>
        );
      })}
    </ul>
  );
}
