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

  // A 401 anywhere settles the cached session to anonymous so stale identity
  // is never reused. This must NOT remove the query: removal makes the still
  // mounted observer refetch /api/auth/me, whose 401 re-fires this listener
  // into an endless refetch loop. Settled null data is fresh and stays put.
  useEffect(() => {
    const clear = () => queryClient.setQueryData(sessionKey, null);
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
