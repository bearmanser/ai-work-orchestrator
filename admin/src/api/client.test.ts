// admin/src/api/client.test.ts
import { describe, expect, it } from "vitest";
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
});
