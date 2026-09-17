import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { http, HttpResponse } from "msw";
import type { ReactElement } from "react";
import { describe, expect, it } from "vitest";
import { server } from "../../test/server";
import type { RepositoryProfile } from "../../api/types";
import { BuildCommandsForm } from "./BuildCommandsForm";

const baseRepository: RepositoryProfile = {
  id: "r1",
  name: "orchestrator-web",
  remoteUrl: "https://dev.azure.com/kameleon/_git/orchestrator-web",
  defaultBranch: "main",
  purpose: "Customer-facing web app",
  ownerContactId: "U1",
  accessPolicy: "Standard",
  buildCommands: {
    restore: "npm ci",
    build: "npm run build",
    test: "npm test",
    lint: "npm run lint",
    workingDirectory: ".",
    timeout: "00:30:00",
  },
  instructionFilePaths: [],
  lastInspectedCommit: "abc123",
  lastInspectedAt: "2026-09-16T10:00:00Z",
  version: 3,
};

function renderWithProviders(ui: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

describe("BuildCommandsForm", () => {
  it("saves build commands and shows a validation error for an empty test command with tests enabled", async () => {
    server.use(http.get("*/api/admin/repositories/r1", () => HttpResponse.json(baseRepository)));
    renderWithProviders(<BuildCommandsForm repositoryId="r1" />);

    await screen.findByDisplayValue("npm test");
    await userEvent.clear(screen.getByLabelText(/test command/i));
    await userEvent.click(screen.getByRole("button", { name: /save/i }));

    expect(await screen.findByRole("alert")).toHaveTextContent(/test command/i);
  });

  it("saves build commands with the current version as If-Match", async () => {
    let seenIfMatch: string | null = null;
    let seenBody: unknown;
    server.use(
      http.get("*/api/admin/repositories/r1", () => HttpResponse.json(baseRepository)),
      http.put("*/api/admin/repositories/r1", async ({ request }) => {
        seenIfMatch = request.headers.get("If-Match");
        seenBody = await request.json();
        return HttpResponse.json({ ...baseRepository, version: 4 });
      }),
    );
    renderWithProviders(<BuildCommandsForm repositoryId="r1" />);

    await screen.findByDisplayValue("npm test");
    await userEvent.click(screen.getByRole("button", { name: /save/i }));

    expect(await screen.findByText(/build commands saved/i)).toBeInTheDocument();
    expect(seenIfMatch).toBe("3");
    expect(seenBody).toMatchObject({ buildCommands: { test: "npm test" } });
  });

  it("allows saving with no build and no test command", async () => {
    const untested = { ...baseRepository, buildCommands: { ...baseRepository.buildCommands, build: null, test: null } };
    server.use(
      http.get("*/api/admin/repositories/r1", () => HttpResponse.json(untested)),
      http.put("*/api/admin/repositories/r1", () => HttpResponse.json(untested)),
    );
    renderWithProviders(<BuildCommandsForm repositoryId="r1" />);

    await screen.findByLabelText(/restore command/i);
    await userEvent.click(screen.getByRole("button", { name: /save/i }));

    expect(await screen.findByText(/build commands saved/i)).toBeInTheDocument();
  });
});
