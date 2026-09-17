import { useState } from "react";
import { Link } from "react-router-dom";
import { TaskState, taskStateName } from "../../api/types";
import { useTaskList } from "./useTasks";

const stateOptions = Object.values(TaskState).filter(
  (value): value is TaskState => typeof value === "number",
);

export function TaskListPage() {
  const [state, setState] = useState<TaskState | undefined>(undefined);
  const [repositoryId, setRepositoryId] = useState("");
  const [page, setPage] = useState(1);
  const pageSize = 20;

  const { data, isLoading, isError } = useTaskList({
    state,
    repositoryId: repositoryId.length > 0 ? repositoryId : undefined,
    page,
    pageSize,
  });

  function handleStateChange(value: string) {
    setPage(1);
    setState(value === "" ? undefined : (Number(value) as TaskState));
  }

  function handleRepositoryIdChange(value: string) {
    setPage(1);
    setRepositoryId(value);
  }

  const totalPages = data === undefined ? 1 : Math.max(1, Math.ceil(data.totalCount / data.pageSize));

  return (
    <main>
      <h1>Tasks</h1>
      <form>
        <label htmlFor="state-filter">State</label>
        <select
          id="state-filter"
          value={state === undefined ? "" : String(state)}
          onChange={(event) => handleStateChange(event.target.value)}
        >
          <option value="">All states</option>
          {stateOptions.map((value) => (
            <option key={value} value={value}>
              {taskStateName(value)}
            </option>
          ))}
        </select>

        <label htmlFor="repository-filter">Repository ID</label>
        <input
          id="repository-filter"
          type="text"
          value={repositoryId}
          onChange={(event) => handleRepositoryIdChange(event.target.value)}
        />
      </form>

      {isLoading && <p>Loading tasks…</p>}
      {isError && <p role="alert">Tasks could not be loaded.</p>}

      {data !== undefined && (
        <>
          <table>
            <thead>
              <tr>
                <th scope="col">Objective</th>
                <th scope="col">State</th>
                <th scope="col">Updated</th>
                <th scope="col">Blocked on</th>
                <th scope="col">Waiting for</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((task) => (
                <tr key={task.id}>
                  <td>
                    <Link to={`/tasks/${task.id}`}>{task.objective}</Link>
                  </td>
                  <td>{taskStateName(task.state)}</td>
                  <td>{new Date(task.updatedAt).toLocaleString()}</td>
                  <td>{task.blockedOn ?? "—"}</td>
                  <td>{task.waitingFor ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>

          {data.items.length === 0 && <p>No tasks match the current filters.</p>}

          <nav aria-label="Pagination">
            <button type="button" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
              Previous
            </button>
            <span>
              Page {page} of {totalPages}
            </span>
            <button type="button" disabled={page >= totalPages} onClick={() => setPage((p) => p + 1)}>
              Next
            </button>
          </nav>
        </>
      )}
    </main>
  );
}
