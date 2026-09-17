import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import type { ReactElement } from "react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { server } from "../../test/server";
import type { RepositoryProfile } from "../../api/types";
import { RepositoryListPage } from "./RepositoryListPage";

const oneRepository: RepositoryProfile = {
  id: "r1",
  name: "orchestrator-web",
  remoteUrl: "https://dev.azure.com/kameleon/_git/orchestrator-web",
  defaultBranch: "main",
  purpose: "Customer-facing web app",
  ownerContactId: "U1",
  accessPolicy: "Standard",
  buildCommands: { restore: null, build: null, test: null, lint: null, workingDirectory: ".", timeout: "00:30:00" },
  instructionFilePaths: [],
  lastInspectedCommit: null,
  lastInspectedAt: null,
  version: 1,
};

function renderWithProviders(ui: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>{ui}</MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("RepositoryListPage", () => {
  it("lists repositories, linking each to its detail page", async () => {
    server.use(http.get("*/api/admin/repositories", () => HttpResponse.json([oneRepository])));
    renderWithProviders(<RepositoryListPage />);

    const link = await screen.findByRole("link", { name: /orchestrator-web/i });
    expect(link).toHaveAttribute("href", "/repositories/r1");
  });

  it("shows a message when the catalog is empty", async () => {
    server.use(http.get("*/api/admin/repositories", () => HttpResponse.json([])));
    renderWithProviders(<RepositoryListPage />);

    expect(await screen.findByText(/no repositories are in the catalog yet/i)).toBeInTheDocument();
  });

  it("adds a repository through the form", async () => {
    let seenBody: unknown;
    server.use(
      http.get("*/api/admin/repositories", () => HttpResponse.json([])),
      http.post("*/api/admin/repositories", async ({ request }) => {
        seenBody = await request.json();
        return HttpResponse.json({ ...oneRepository, id: "r2", name: "new-repo" }, { status: 201 });
      }),
    );
    renderWithProviders(<RepositoryListPage />);
    await screen.findByText(/no repositories are in the catalog yet/i);

    await userEvent.type(screen.getByLabelText(/^name$/i), "new-repo");
    await userEvent.type(screen.getByLabelText(/remote url/i), "https://dev.azure.com/kameleon/_git/new-repo");
    await userEvent.type(screen.getByLabelText(/purpose/i), "A new service");
    await userEvent.type(screen.getByLabelText(/owner contact/i), "U2");
    await userEvent.click(screen.getByRole("button", { name: /add repository/i }));

    expect(seenBody).toMatchObject({ name: "new-repo", ownerContactId: "U2" });
  });
});
