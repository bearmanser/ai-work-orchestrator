import { useQuery } from "@tanstack/react-query";
import { apiClient } from "../../api/client";
import type { PagedResult, TaskDetail, TaskListQuery, TaskSummary } from "../../api/types";
import { taskStateName } from "../../api/types";

function buildTaskListPath(query: TaskListQuery): string {
  const params = new URLSearchParams();
  if (query.state !== undefined) params.set("state", taskStateName(query.state));
  if (query.repositoryId !== undefined && query.repositoryId.length > 0) {
    params.set("repositoryId", query.repositoryId);
  }
  if (query.page !== undefined) params.set("page", String(query.page));
  if (query.pageSize !== undefined) params.set("pageSize", String(query.pageSize));

  const search = params.toString();
  return search.length > 0 ? `/api/admin/tasks?${search}` : "/api/admin/tasks";
}

/** GET /api/admin/tasks - the filterable, paginated task list. */
export function useTaskList(query: TaskListQuery) {
  return useQuery({
    queryKey: ["tasks", "list", query],
    queryFn: () => apiClient.get<PagedResult<TaskSummary>>(buildTaskListPath(query)),
  });
}

/** GET /api/admin/tasks/{id} - the full task detail, including evidence. */
export function useTaskDetail(taskId: string | undefined) {
  return useQuery({
    queryKey: ["tasks", "detail", taskId],
    queryFn: () => apiClient.get<TaskDetail>(`/api/admin/tasks/${taskId}`),
    enabled: taskId !== undefined && taskId.length > 0,
  });
}
