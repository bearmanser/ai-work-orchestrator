import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import { usePolicyRules, useUpdatePolicyRules } from "./useSettings";

export function PolicyPage() {
  const { data: rules, isLoading, isError } = usePolicyRules();
  const updateRules = useUpdatePolicyRules();
  const [forbiddenEnvironments, setForbiddenEnvironments] = useState("");
  const [error, setError] = useState("");
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    if (rules !== undefined) {
      setForbiddenEnvironments(rules.forbiddenEnvironments.join(", "));
    }
  }, [rules]);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError("");
    setSaved(false);

    const forbidden = forbiddenEnvironments
      .split(",")
      .map((value) => value.trim())
      .filter((value) => value.length > 0);

    try {
      await updateRules.mutateAsync({ forbiddenEnvironments: forbidden });
      setSaved(true);
    } catch (submitError) {
      setError(submitError instanceof Error ? submitError.message : "Could not save policy rules.");
    }
  }

  return (
    <main>
      <h1>Policy</h1>
      <p>
        Production and prod are a documented non-goal, not a configurable setting: they cannot be removed from the
        forbidden environments list here, and the server rejects any attempt to do so.
      </p>

      {isLoading && <p>Loading policy…</p>}
      {isError && <p role="alert">Policy rules could not be loaded.</p>}

      {rules !== undefined && (
        <>
          <section aria-label="Actions always requiring approval">
            <h2>Actions that always require approval</h2>
            <ul>
              {rules.alwaysRequiresApproval.map((action) => (
                <li key={action}>{action}</li>
              ))}
            </ul>
          </section>

          <form onSubmit={handleSubmit}>
            <label htmlFor="forbidden-environments">Forbidden environments (comma-separated)</label>
            <input
              id="forbidden-environments"
              type="text"
              value={forbiddenEnvironments}
              onChange={(event) => setForbiddenEnvironments(event.target.value)}
            />
            <button type="submit" disabled={updateRules.isPending}>
              Save
            </button>
          </form>

          {error.length > 0 && <p role="alert">{error}</p>}
          {saved && error.length === 0 && <p>Policy rules saved.</p>}
        </>
      )}
    </main>
  );
}
