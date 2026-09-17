import type { AuditEntrySummary } from "../../api/types";

export interface AuditTrailProps {
  entries: AuditEntrySummary[];
}

function formatOccurredAt(occurredAt: string): string {
  const parsed = new Date(occurredAt);
  return Number.isNaN(parsed.getTime()) ? occurredAt : parsed.toLocaleString();
}

/** Renders the immutable audit trail for a task, oldest first as the API returns it. */
export function AuditTrail({ entries }: AuditTrailProps) {
  if (entries.length === 0) {
    return (
      <section aria-label="Audit trail">
        <h2>Audit trail</h2>
        <p>No audit entries recorded for this task.</p>
      </section>
    );
  }

  return (
    <section aria-label="Audit trail">
      <h2>Audit trail</h2>
      <table>
        <thead>
          <tr>
            <th scope="col">When</th>
            <th scope="col">Actor</th>
            <th scope="col">Action</th>
            <th scope="col">Reason</th>
            <th scope="col">Outcome</th>
          </tr>
        </thead>
        <tbody>
          {entries.map((entry) => (
            <tr key={entry.id}>
              <td>{formatOccurredAt(entry.occurredAt)}</td>
              <td>{entry.actor}</td>
              <td>{entry.action}</td>
              <td>{entry.reason}</td>
              <td>{entry.outcome}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
