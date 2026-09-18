// admin/src/features/auth/useSession.test.tsx
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { describe, expect, it } from "vitest";
import { server } from "../../test/server";
import { useSession } from "./useSession";

function Probe() {
  const { session, isLoading } = useSession();
  return <p>{isLoading ? "loading" : session === null ? "anonymous" : session.loginName}</p>;
}

describe("useSession", () => {
  it("settles on anonymous instead of refetching /api/auth/me forever after a 401", async () => {
    let hits = 0;
    server.use(http.get("/api/auth/me", () => {
      hits += 1;
      return HttpResponse.json({ detail: "Unauthorized" }, { status: 401 });
    }));
    const client = new QueryClient();
    render(
      <QueryClientProvider client={client}>
        <Probe />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(document.body.textContent).toContain("anonymous"));
    const settledHits = hits;
    // Give any refetch loop a chance to fire more requests.
    await new Promise((resolve) => setTimeout(resolve, 500));
    expect(hits).toBe(settledHits);
  });
});
