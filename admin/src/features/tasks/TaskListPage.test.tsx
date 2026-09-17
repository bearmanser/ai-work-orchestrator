import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import type { ReactElement } from "react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { server } from "../../test/server";
import type { PagedResult, TaskSummary } from "../../api/types";
import { TaskListPage } from "./TaskListPage";

function renderWithProviders(ui: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>{ui}</MemoryRouter>
    </QueryClientProvider>,
  );
}

const oneTask: TaskSummary = {
  id: "t1",
  objective: "Fix the flaky retry test",
  state: 1,
  requesterId: "U1",
  updatedAt: "2026-09-17T10:00:00Z",
  blockedOn: null,
  waitingFor: null,
};

describe("TaskListPage", () => {
  it("lists tasks returned by the API, linking each to its detail page", async () => {
    server.use(
      http.get("*/api/admin/tasks", () =>
        HttpResponse.json({ items: [oneTask], page: 1, pageSize: 20, totalCount: 1 } satisfies PagedResult<TaskSummary>),
      ),
    );

    renderWithProviders(<TaskListPage />);

    const link = await screen.findByRole("link", { name: /fix the flaky retry test/i });
    expect(link).toHaveAttribute("href", "/tasks/t1");
  });

  it("shows a message when no tasks match the current filters", async () => {
    server.use(
      http.get("*/api/admin/tasks", () =>
        HttpResponse.json({ items: [], page: 1, pageSize: 20, totalCount: 0 } satisfies PagedResult<TaskSummary>),
      ),
    );

    renderWithProviders(<TaskListPage />);

    expect(await screen.findByText(/no tasks match the current filters/i)).toBeInTheDocument();
  });

  it("refetches with a repositoryId filter when one is entered", async () => {
    let lastUrl = "";
    server.use(
      http.get("*/api/admin/tasks", ({ request }) => {
        lastUrl = request.url;
        return HttpResponse.json({ items: [], page: 1, pageSize: 20, totalCount: 0 } satisfies PagedResult<TaskSummary>);
      }),
    );

    renderWithProviders(<TaskListPage />);
    await screen.findByText(/no tasks match the current filters/i);

    await userEvent.type(screen.getByLabelText(/repository id/i), "11111111-1111-1111-1111-111111111111");

    await screen.findByText(/no tasks match the current filters/i);
    expect(lastUrl).toContain("repositoryId=11111111-1111-1111-1111-111111111111");
  });
});
