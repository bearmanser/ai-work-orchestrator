import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { apiClient } from "../../api/client";
import type {
  CreateOwnershipRequest,
  CreatePersonRequest,
  IntegrationStatus,
  OwnershipDto,
  PersonDto,
  PolicyRulesDto,
  SetIntegrationSecretRequest,
  UpdatePolicyRulesRequest,
} from "../../api/types";

/** GET /api/admin/integrations - status of every known integration. */
export function useIntegrations() {
  return useQuery({
    queryKey: ["settings", "integrations"],
    queryFn: () => apiClient.get<IntegrationStatus[]>("/api/admin/integrations"),
  });
}

/** POST /api/admin/integrations/{name}/test - triggers a live connectivity check. */
export function useTestIntegration() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (name: string) => apiClient.post<IntegrationStatus>(`/api/admin/integrations/${name}/test`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["settings", "integrations"] }),
  });
}

/** PUT /api/admin/integrations/{name} - configures the secret reference an integration uses. */
export function useSetIntegrationSecretRef() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ name, secretRef }: { name: string; secretRef: string }) =>
      apiClient.put<void>(`/api/admin/integrations/${name}`, { secretRef } satisfies SetIntegrationSecretRequest),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["settings", "integrations"] }),
  });
}

/** GET /api/admin/policy - the current policy rules. */
export function usePolicyRules() {
  return useQuery({
    queryKey: ["settings", "policy"],
    queryFn: () => apiClient.get<PolicyRulesDto>("/api/admin/policy"),
  });
}

/** PUT /api/admin/policy - updates the policy rules. */
export function useUpdatePolicyRules() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: UpdatePolicyRulesRequest) => apiClient.put<void>("/api/admin/policy", request),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["settings", "policy"] }),
  });
}

/** GET /api/admin/people - the identity directory. */
export function usePeople() {
  return useQuery({
    queryKey: ["settings", "people"],
    queryFn: () => apiClient.get<PersonDto[]>("/api/admin/people"),
  });
}

/** POST /api/admin/people - adds a person to the identity directory. */
export function useCreatePerson() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: CreatePersonRequest) => apiClient.post<PersonDto>("/api/admin/people", request),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["settings", "people"] }),
  });
}

/** DELETE /api/admin/people/{id} - removes a person from the identity directory. */
export function useDeletePerson() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.del<void>(`/api/admin/people/${id}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["settings", "people"] }),
  });
}

/** GET /api/admin/ownership - the ownership directory. */
export function useOwnership() {
  return useQuery({
    queryKey: ["settings", "ownership"],
    queryFn: () => apiClient.get<OwnershipDto[]>("/api/admin/ownership"),
  });
}

/** POST /api/admin/ownership - grants ownership of a repository or domain to a person. */
export function useCreateOwnership() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: CreateOwnershipRequest) => apiClient.post<OwnershipDto>("/api/admin/ownership", request),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["settings", "ownership"] }),
  });
}

/** DELETE /api/admin/ownership/{id} - revokes an ownership grant. */
export function useDeleteOwnership() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.del<void>(`/api/admin/ownership/${id}`),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["settings", "ownership"] }),
  });
}
