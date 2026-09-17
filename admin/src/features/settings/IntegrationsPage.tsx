import { useState } from "react";
import { useIntegrations, useSetIntegrationSecretRef, useTestIntegration } from "./useSettings";

export function IntegrationsPage() {
  const { data: integrations, isLoading, isError } = useIntegrations();
  const testIntegration = useTestIntegration();
  const setSecretRef = useSetIntegrationSecretRef();
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});

  async function handleSave(name: string) {
    const secretRef = (drafts[name] ?? "").trim();
    if (secretRef.length === 0) return;

    setErrors((prev) => ({ ...prev, [name]: "" }));
    try {
      await setSecretRef.mutateAsync({ name, secretRef });
      setDrafts((prev) => ({ ...prev, [name]: "" }));
    } catch (error) {
      setErrors((prev) => ({
        ...prev,
        [name]: error instanceof Error ? error.message : "Could not save the secret reference.",
      }));
    }
  }

  return (
    <main>
      <h1>Integrations</h1>
      <p>
        Each integration resolves credentials through a secret reference, never a raw value. This page can only
        display a fingerprint of the configured secret and accept a new reference key — it never shows or accepts
        the secret itself.
      </p>

      {isLoading && <p>Loading integrations…</p>}
      {isError && <p role="alert">Integrations could not be loaded.</p>}

      {integrations !== undefined && (
        <table>
          <thead>
            <tr>
              <th scope="col">Integration</th>
              <th scope="col">Configured</th>
              <th scope="col">Fingerprint</th>
              <th scope="col">Health</th>
              <th scope="col">Last checked</th>
              <th scope="col">Secret reference</th>
            </tr>
          </thead>
          <tbody>
            {integrations.map((integration) => (
              <tr key={integration.name}>
                <td>{integration.name}</td>
                <td>{integration.configured ? "Yes" : "No"}</td>
                <td>{integration.secretFingerprint.length > 0 ? integration.secretFingerprint : "—"}</td>
                <td>
                  {integration.health}
                  {integration.detail !== null && integration.detail.length > 0 && (
                    <span> ({integration.detail})</span>
                  )}
                </td>
                <td>{integration.lastCheckedAt === null ? "Never" : new Date(integration.lastCheckedAt).toLocaleString()}</td>
                <td>
                  <label htmlFor={`secret-ref-${integration.name}`}>Secret reference for {integration.name}</label>
                  <input
                    id={`secret-ref-${integration.name}`}
                    type="text"
                    placeholder="e.g. azuredevops.pat"
                    value={drafts[integration.name] ?? ""}
                    onChange={(event) =>
                      setDrafts((prev) => ({ ...prev, [integration.name]: event.target.value }))
                    }
                  />
                  <button type="button" onClick={() => handleSave(integration.name)}>
                    Save
                  </button>
                  <button
                    type="button"
                    onClick={() => testIntegration.mutate(integration.name)}
                    disabled={testIntegration.isPending}
                  >
                    Test connection
                  </button>
                  {errors[integration.name] !== undefined && errors[integration.name].length > 0 && (
                    <p role="alert">{errors[integration.name]}</p>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </main>
  );
}
