import type { CheckResultSummary, DomainCheckSummary } from "../../api/types";

export interface EvidencePanelProps {
  checks: CheckResultSummary[];
  domainChecks: DomainCheckSummary[];
}

function formatCheckTime(runAt: string): string {
  const parsed = new Date(runAt);
  return Number.isNaN(parsed.getTime()) ? runAt : parsed.toLocaleString();
}

/**
 * Runtime ("local") checks and P1/domain checks are never merged into one
 * list - that conflation is an explicit architectural rule elsewhere in this
 * plan, since the two evidence kinds mean different things (did the code
 * pass its own tests vs. did it satisfy a real-world domain invariant). Each
 * gets its own labelled <section>, so the distinction survives in the DOM
 * and is queryable by assistive tech and tests alike.
 */
export function EvidencePanel({ checks, domainChecks }: EvidencePanelProps) {
  return (
    <div>
      <section aria-label="Local checks">
        <h2>Local checks</h2>
        {checks.length === 0 ? (
          <p>No local checks have run yet.</p>
        ) : (
          <ul>
            {checks.map((check) => (
              <li key={`${check.name}-${check.runAt}`}>
                <strong>{check.name}</strong> — {check.outcome}{" "}
                <span>{formatCheckTime(check.runAt)}</span>
              </li>
            ))}
          </ul>
        )}
      </section>
      <section aria-label="P1 checks">
        <h2>P1 checks</h2>
        {domainChecks.length === 0 ? (
          <p>No P1 validation was performed for this task.</p>
        ) : (
          <ul>
            {domainChecks.map((check) => (
              <li key={`${check.name}-${check.runAt}`}>
                <strong>{check.name}</strong> — {check.outcome}{" "}
                <span>{formatCheckTime(check.runAt)}</span>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
