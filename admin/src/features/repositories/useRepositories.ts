import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiClient } from "../../api/client";
import type {
  CreateRepositoryRequest,
  RepositoryProfile,
  RepositoryRelationship,
  UpdateRepositoryRequest,
} from "../../api/types";

/** GET /api/admin/repositories - the repository catalog. */
export function useRepositoryList() {
  return useQuery({
    queryKey: ["repositories", "list"],
    queryFn: () => apiClient.get<RepositoryProfile[]>("/api/admin/repositories"),
  });
}

/** GET /api/admin/repositories/{id} - a single repository's profile. */
export function useRepositoryDetail(repositoryId: string | undefined) {
  return useQuery({
    queryKey: ["repositories", "detail", repositoryId],
    queryFn: () => apiClient.get<RepositoryProfile>(`/api/admin/repositories/${repositoryId}`),
    enabled: repositoryId !== undefined && repositoryId.length > 0,
  });
}

/** POST /api/admin/repositories - add a repository to the catalog. */
export function useCreateRepository() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: CreateRepositoryRequest) =>
      apiClient.post<RepositoryProfile>("/api/admin/repositories", request),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["repositories", "list"] });
    },
  });
}

/**
 * PUT /api/admin/repositories/{id} - update a repository, including build/test
 * commands. The server requires an `If-Match` header carrying the profile's
 * current version for optimistic concurrency, so callers pass it alongside
 * the request body rather than the hook re-fetching it itself.
 */
export function useUpdateRepository(repositoryId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ request, expectedVersion }: { request: UpdateRepositoryRequest; expectedVersion: number }) =>
      apiClient.put<RepositoryProfile>(`/api/admin/repositories/${repositoryId}`, request, {
        "If-Match": String(expectedVersion),
      }),
    onSuccess: (updated) => {
      queryClient.setQueryData(["repositories", "detail", repositoryId], updated);
      void queryClient.invalidateQueries({ queryKey: ["repositories", "list"] });
    },
  });
}

/** GET /api/admin/repositories/{id}/relationships - relationships for a repository, with evidence. */
export function useRelationships(repositoryId: string | undefined) {
  return useQuery({
    queryKey: ["repositories", "relationships", repositoryId],
    queryFn: () =>
      apiClient.get<RepositoryRelationship[]>(`/api/admin/repositories/${repositoryId}/relationships`),
    enabled: repositoryId !== undefined && repositoryId.length > 0,
  });
}

/**
 * DELETE /api/admin/relationships/{relationshipId} - remove an admin-asserted
 * or conflicted relationship. The server rejects this for `Evidenced`
 * relationships (409); the UI already disables the control for those, this
 * just surfaces the error if it happens anyway.
 */
export function useDeleteRelationship(repositoryId: string | undefined) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (relationshipId: string) =>
      apiClient.del<void>(`/api/admin/relationships/${relationshipId}`),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["repositories", "relationships", repositoryId] });
    },
  });
}
