import { useState } from "react";
import type { FormEvent } from "react";
import {
  useCreateOwnership,
  useCreatePerson,
  useDeleteOwnership,
  useDeletePerson,
  useOwnership,
  usePeople,
} from "./useSettings";

export function PeoplePage() {
  const { data: people, isLoading: peopleLoading, isError: peopleError } = usePeople();
  const { data: ownership, isLoading: ownershipLoading, isError: ownershipError } = useOwnership();
  const createPerson = useCreatePerson();
  const deletePerson = useDeletePerson();
  const createOwnership = useCreateOwnership();
  const deleteOwnership = useDeleteOwnership();

  const [displayName, setDisplayName] = useState("");
  const [slackUserId, setSlackUserId] = useState("");
  const [azureDevOpsId, setAzureDevOpsId] = useState("");
  const [email, setEmail] = useState("");
  const [personError, setPersonError] = useState("");

  const [subject, setSubject] = useState("Repository");
  const [subjectKey, setSubjectKey] = useState("");
  const [ownerPersonId, setOwnerPersonId] = useState("");
  const [canConfirmPlans, setCanConfirmPlans] = useState(false);
  const [canApproveRestrictedActions, setCanApproveRestrictedActions] = useState(false);
  const [ownershipError2, setOwnershipError2] = useState("");

  async function handleCreatePerson(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setPersonError("");
    try {
      await createPerson.mutateAsync({
        displayName,
        slackUserId: slackUserId.length > 0 ? slackUserId : null,
        azureDevOpsId: azureDevOpsId.length > 0 ? azureDevOpsId : null,
        email: email.length > 0 ? email : null,
      });
      setDisplayName("");
      setSlackUserId("");
      setAzureDevOpsId("");
      setEmail("");
    } catch (error) {
      setPersonError(error instanceof Error ? error.message : "Could not add the person.");
    }
  }

  async function handleCreateOwnership(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setOwnershipError2("");
    try {
      await createOwnership.mutateAsync({
        subject,
        subjectKey,
        ownerPersonId,
        canConfirmPlans,
        canApproveRestrictedActions,
      });
      setSubjectKey("");
      setOwnerPersonId("");
      setCanConfirmPlans(false);
      setCanApproveRestrictedActions(false);
    } catch (error) {
      setOwnershipError2(error instanceof Error ? error.message : "Could not add the ownership grant.");
    }
  }

  return (
    <main>
      <h1>People and ownership</h1>

      <section aria-label="People">
        <h2>People</h2>
        {peopleLoading && <p>Loading people…</p>}
        {peopleError && <p role="alert">People could not be loaded.</p>}
        {people !== undefined && (
          <table>
            <thead>
              <tr>
                <th scope="col">Name</th>
                <th scope="col">Slack</th>
                <th scope="col">Azure DevOps</th>
                <th scope="col">Email</th>
                <th scope="col">Active</th>
                <th scope="col" />
              </tr>
            </thead>
            <tbody>
              {people.map((person) => (
                <tr key={person.id}>
                  <td>{person.displayName}</td>
                  <td>{person.slackUserId ?? "—"}</td>
                  <td>{person.azureDevOpsId ?? "—"}</td>
                  <td>{person.email ?? "—"}</td>
                  <td>{person.isActive ? "Yes" : "No"}</td>
                  <td>
                    <button type="button" onClick={() => deletePerson.mutate(person.id)}>
                      Remove
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        <form onSubmit={handleCreatePerson}>
          <h3>Add a person</h3>
          <label htmlFor="person-display-name">Display name</label>
          <input
            id="person-display-name"
            type="text"
            required
            value={displayName}
            onChange={(event) => setDisplayName(event.target.value)}
          />

          <label htmlFor="person-slack-id">Slack user ID</label>
          <input
            id="person-slack-id"
            type="text"
            value={slackUserId}
            onChange={(event) => setSlackUserId(event.target.value)}
          />

          <label htmlFor="person-ado-id">Azure DevOps ID</label>
          <input
            id="person-ado-id"
            type="text"
            value={azureDevOpsId}
            onChange={(event) => setAzureDevOpsId(event.target.value)}
          />

          <label htmlFor="person-email">Email</label>
          <input id="person-email" type="email" value={email} onChange={(event) => setEmail(event.target.value)} />

          <button type="submit">Add person</button>
          {personError.length > 0 && <p role="alert">{personError}</p>}
        </form>
      </section>

      <section aria-label="Ownership">
        <h2>Ownership</h2>
        {ownershipLoading && <p>Loading ownership…</p>}
        {ownershipError && <p role="alert">Ownership could not be loaded.</p>}
        {ownership !== undefined && (
          <table>
            <thead>
              <tr>
                <th scope="col">Subject</th>
                <th scope="col">Key</th>
                <th scope="col">Owner</th>
                <th scope="col">Can confirm plans</th>
                <th scope="col">Can approve restricted actions</th>
                <th scope="col" />
              </tr>
            </thead>
            <tbody>
              {ownership.map((entry) => (
                <tr key={entry.id}>
                  <td>{entry.subject}</td>
                  <td>{entry.subjectKey}</td>
                  <td>{people?.find((p) => p.id === entry.ownerPersonId)?.displayName ?? entry.ownerPersonId}</td>
                  <td>{entry.canConfirmPlans ? "Yes" : "No"}</td>
                  <td>{entry.canApproveRestrictedActions ? "Yes" : "No"}</td>
                  <td>
                    <button type="button" onClick={() => deleteOwnership.mutate(entry.id)}>
                      Remove
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        <form onSubmit={handleCreateOwnership}>
          <h3>Grant ownership</h3>
          <label htmlFor="ownership-subject">Subject</label>
          <select id="ownership-subject" value={subject} onChange={(event) => setSubject(event.target.value)}>
            <option value="Repository">Repository</option>
            <option value="Domain">Domain</option>
          </select>

          <label htmlFor="ownership-subject-key">Subject key</label>
          <input
            id="ownership-subject-key"
            type="text"
            required
            value={subjectKey}
            onChange={(event) => setSubjectKey(event.target.value)}
          />

          <label htmlFor="ownership-owner">Owner</label>
          <select
            id="ownership-owner"
            required
            value={ownerPersonId}
            onChange={(event) => setOwnerPersonId(event.target.value)}
          >
            <option value="">Select a person</option>
            {people?.map((person) => (
              <option key={person.id} value={person.id}>
                {person.displayName}
              </option>
            ))}
          </select>

          <label htmlFor="ownership-can-confirm">
            <input
              id="ownership-can-confirm"
              type="checkbox"
              checked={canConfirmPlans}
              onChange={(event) => setCanConfirmPlans(event.target.checked)}
            />
            Can confirm plans
          </label>

          <label htmlFor="ownership-can-approve">
            <input
              id="ownership-can-approve"
              type="checkbox"
              checked={canApproveRestrictedActions}
              onChange={(event) => setCanApproveRestrictedActions(event.target.checked)}
            />
            Can approve restricted actions
          </label>

          <button type="submit">Grant ownership</button>
          {ownershipError2.length > 0 && <p role="alert">{ownershipError2}</p>}
        </form>
      </section>
    </main>
  );
}
