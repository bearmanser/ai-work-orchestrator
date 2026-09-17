import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, within } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import type { ReactElement } from "react";
import { describe, expect, it } from "vitest";
import { server } from "../../test/server";
import type { TaskDetail } from "../../api/types";
import { TaskDetailPage } from "./TaskDetailPage";

const baseTask: TaskDetail = {
  id: "t1",
  objective: "Implement task inspection",
  state: 8, // WatchingPullRequests
  requesterId: "U1",
  updatedAt: "2026-09-17T10:00:00Z",
  blockedOn: null,
  waitingFor: null,
  steps: [],
  pullRequests: [],
  checks: [{ name: "dotnet test", outcome: "Passed", runAt: "2026-09-17T09:00:00Z" }],
  domainChecks: [{ name: "picked-order-count", outcome: "Passed", runAt: "2026-09-17T09:05:00Z" }],
  confirmedPlan: null,
  budget: {
    maxUsd: 10,
    spentUsd: 1,
    maxWallClock: "01:00:00",
    elapsedWallClock: "00:05:00",
    maxAgentInvocations: 5,
    agentInvocations: 1,
    maxCorrectionAttempts: 3,
    correctionAttempts: 0,
  },
  audit: [],
};

function taskDetailHandler(overrides: Partial<TaskDetail>) {
  return http.get("*/api/admin/tasks/t1", () => HttpResponse.json({ ...baseTask, ...overrides }));
}

function renderWithProviders(ui: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

describe("TaskDetailPage", () => {
  it("shows local checks and P1 checks under separate headings", async () => {
    server.use(taskDetailHandler({}));
    renderWithProviders(<TaskDetailPage taskId="t1" />);

    const local = await screen.findByRole("region", { name: /local checks/i });
    const domain = await screen.findByRole("region", { name: /p1 checks/i });

    expect(within(local).getByText(/dotnet test/)).toBeInTheDocument();
    expect(within(domain).getByText(/picked-order-count/)).toBeInTheDocument();
  });

  it("states when no P1 validation was performed", async () => {
    server.use(taskDetailHandler({ domainChecks: [] }));
    renderWithProviders(<TaskDetailPage taskId="t1" />);

    expect(await screen.findByText(/no p1 validation was performed/i)).toBeInTheDocument();
  });

  it("shows who the task is waiting for when blocked", async () => {
    server.use(
      taskDetailHandler({
        state: 2, // WaitingForHuman
        waitingFor: "Peer",
        blockedOn: "question",
      }),
    );
    renderWithProviders(<TaskDetailPage taskId="t1" />);

    expect(await screen.findByText(/waiting for Peer/i)).toBeInTheDocument();
  });

  it("lists every required pull request with its approval state", async () => {
    server.use(
      taskDetailHandler({
        pullRequests: [
          { pullRequestId: 4711, url: "https://example.test/pr/4711", isRequired: true, isApproved: true, approvalReason: "approved by Magnus" },
          { pullRequestId: 4712, url: "https://example.test/pr/4712", isRequired: true, isApproved: false, approvalReason: "1 active comment thread" },
        ],
      }),
    );
    renderWithProviders(<TaskDetailPage taskId="t1" />);

    expect(await screen.findByText(/1 active comment thread/i)).toBeInTheDocument();
  });

  it("shows an error when the task cannot be loaded", async () => {
    server.use(http.get("*/api/admin/tasks/t1", () => new HttpResponse(null, { status: 500 })));
    renderWithProviders(<TaskDetailPage taskId="t1" />);

    expect(await screen.findByRole("alert")).toHaveTextContent(/could not be loaded/i);
  });
});
