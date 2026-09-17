import { taskStateName } from "../../api/types";
import { AuditTrail } from "./AuditTrail";
import { EvidencePanel } from "./EvidencePanel";
import { useTaskDetail } from "./useTasks";

export interface TaskDetailPageProps {
  taskId: string;
}

export function TaskDetailPage({ taskId }: TaskDetailPageProps) {
  const { data: task, isLoading, isError } = useTaskDetail(taskId);

  if (isLoading) {
    return <p>Loading task…</p>;
  }
  if (isError || task === undefined) {
    return <p role="alert">This task could not be loaded.</p>;
  }

  return (
    <main>
      <h1>{task.objective}</h1>
      <dl>
        <dt>State</dt>
        <dd>{taskStateName(task.state)}</dd>

        {task.blockedOn !== null && (
          <>
            <dt>Blocked on</dt>
            <dd>{task.blockedOn}</dd>
          </>
        )}

        {task.waitingFor !== null && (
          <>
            <dt>Waiting for</dt>
            <dd>Waiting for {task.waitingFor}</dd>
          </>
        )}
      </dl>

      <section aria-label="Steps">
        <h2>Steps</h2>
        {task.steps.length === 0 ? (
          <p>No steps recorded yet.</p>
        ) : (
          <ul>
            {task.steps.map((step) => (
              <li key={step.id}>
                {step.capability} — {step.status}
              </li>
            ))}
          </ul>
        )}
      </section>

      <section aria-label="Pull requests">
        <h2>Pull requests</h2>
        {task.pullRequests.length === 0 ? (
          <p>No pull requests are tracked for this task.</p>
        ) : (
          <ul>
            {task.pullRequests.map((pr) => (
              <li key={pr.pullRequestId}>
                <a href={pr.url}>{pr.url}</a> —{" "}
                {pr.isApproved ? "Approved" : "Not approved"}
                {pr.approvalReason !== null && <span> ({pr.approvalReason})</span>}
              </li>
            ))}
          </ul>
        )}
      </section>

      <EvidencePanel checks={task.checks} domainChecks={task.domainChecks} />

      <section aria-label="Budget">
        <h2>Budget</h2>
        <p>
          ${task.budget.spentUsd.toFixed(2)} of ${task.budget.maxUsd.toFixed(2)} spent
        </p>
      </section>

      <AuditTrail entries={task.audit} />
    </main>
  );
}
