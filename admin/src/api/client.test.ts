// admin/src/api/client.test.ts
import { describe, expect, it, vi } from "vitest";
import { apiClient, ApiError } from "./client";
import { server } from "../test/server";
import { http, HttpResponse } from "msw";

describe("apiClient", () => {
  it("sends the CSRF header on unsafe verbs", async () => {
    document.cookie = "orchestrator_csrf=token-123";
    let seen: string | null = null;
    server.use(http.post("/api/admin/x", ({ request }) => {
      seen = request.headers.get("X-CSRF-Token");
      return HttpResponse.json({});
    }));

    await apiClient.post("/api/admin/x", {});
    expect(seen).toBe("token-123");
  });

  it("omits the CSRF header on GET", async () => {
    let seen: string | null = "unset";
    server.use(http.get("/api/admin/x", ({ request }) => {
      seen = request.headers.get("X-CSRF-Token");
      return HttpResponse.json({});
    }));

    await apiClient.get("/api/admin/x");
    expect(seen).toBeNull();
  });

  it("throws ApiError carrying the status and detail", async () => {
    server.use(http.get("/api/admin/x", () =>
      HttpResponse.json({ detail: "nope" }, { status: 403 })));

    await expect(apiClient.get("/api/admin/x")).rejects.toBeInstanceOf(ApiError);
    await expect(apiClient.get("/api/admin/x")).rejects.toMatchObject({ status: 403, detail: "nope" });
  });

  describe("session-expiry signalling", () => {
    // The client must never navigate itself: the anonymous session check
    // 401s even on /login, so a hard navigation reloads into an infinite
    // loop. It only dispatches unauthorizedEvent; RequireSession bounces
    // through the router. (jsdom cannot observe real navigation, so these
    // tests pin the event contract plus the rejection callers rely on.)
    it("dispatches unauthorizedEvent and rejects on a 401", async () => {
      window.history.pushState({}, "", "/login");
      const seen: string[] = [];
      const listener = (event: Event) => seen.push(event.type);
      window.addEventListener("orchestrator:unauthorized", listener);
      try {
        server.use(http.get("/api/auth/me", () =>
          HttpResponse.json({ detail: "Unauthorized" }, { status: 401 })));

        await expect(apiClient.get("/api/auth/me")).rejects.toMatchObject({ status: 401 });
        expect(seen).toEqual(["orchestrator:unauthorized"]);
      } finally {
        window.removeEventListener("orchestrator:unauthorized", listener);
        window.history.pushState({}, "", "/");
        vi.restoreAllMocks();
      }
    });

    it("dispatches unauthorizedEvent on a 401 from any other page too", async () => {
      window.history.pushState({}, "", "/tasks");
      const seen: string[] = [];
      const listener = (event: Event) => seen.push(event.type);
      window.addEventListener("orchestrator:unauthorized", listener);
      try {
        server.use(http.get("/api/tasks", () =>
          HttpResponse.json({ detail: "Unauthorized" }, { status: 401 })));

        await expect(apiClient.get("/api/tasks")).rejects.toMatchObject({ status: 401 });
        expect(seen).toEqual(["orchestrator:unauthorized"]);
      } finally {
        window.removeEventListener("orchestrator:unauthorized", listener);
        window.history.pushState({}, "", "/");
        vi.restoreAllMocks();
      }
    });
  });
});
