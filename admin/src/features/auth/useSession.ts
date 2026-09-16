import { useEffect } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { apiClient, unauthorizedEvent } from "../../api/client";
import type { LoginRequest, SessionInfo } from "../../api/types";

const sessionKey = ["session"] as const;

export interface SessionState {
  session: SessionInfo | null;
  isLoading: boolean;
  login: (loginName: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
}

export function useSession(): SessionState {
  const queryClient = useQueryClient();
  const query = useQuery({
    queryKey: sessionKey,
    queryFn: () => apiClient.get<SessionInfo>("/api/auth/me"),
    retry: false,
  });

  // A 401 anywhere clears the cached session so stale identity is never reused.
  useEffect(() => {
    const clear = () => queryClient.removeQueries({ queryKey: sessionKey });
    window.addEventListener(unauthorizedEvent, clear);
    return () => window.removeEventListener(unauthorizedEvent, clear);
  }, [queryClient]);

  async function login(loginName: string, password: string): Promise<void> {
    const body: LoginRequest = { loginName, password };
    await apiClient.post("/api/auth/login", body);
    await queryClient.invalidateQueries({ queryKey: sessionKey });
  }

  async function logout(): Promise<void> {
    await apiClient.post("/api/auth/logout");
    queryClient.removeQueries({ queryKey: sessionKey });
  }

  return {
    session: query.data ?? null,
    isLoading: query.isLoading,
    login,
    logout,
  };
}
