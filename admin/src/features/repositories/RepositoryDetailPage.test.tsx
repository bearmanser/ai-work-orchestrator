import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import type { ReactElement } from "react";
import { describe, expect, it } from "vitest";
import { server } from "../../test/server";
import type { RepositoryProfile, RepositoryRelationship } from "../../api/types";
import { RepositoryDetailPage } from "./RepositoryDetailPage";

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
  version: 1,
};

function repositoryHandler(overrides: Partial<RepositoryProfile> = {}) {
  return http.get("*/api/admin/repositories/r1", () => HttpResponse.json({ ...baseRepository, ...overrides }));
}

function relationshipsHandler(relationships: RepositoryRelationship[]) {
  return http.get("*/api/admin/repositories/r1/relationships", () => HttpResponse.json(relationships));
}

function renderWithProviders(ui: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}>{ui}</QueryClientProvider>);
}

const evidencedTarget = "22222222-2222-2222-2222-222222222222";

describe("RepositoryDetailPage", () => {
  it("shows each relationship with its evidence path and commit", async () => {
    server.use(
      repositoryHandler(),
      relationshipsHandler([
        {
          id: "rel1",
          sourceRepositoryId: "r1",
          targetRepositoryId: evidencedTarget,
          relationshipType: "ConsumesHttpApi",
          status: "Evidenced",
          evidence: [{ sourcePath: "src/Web/appsettings.json", commitSha: "abc123", excerpt: "https://backend" }],
          lastVerifiedAt: "2026-09-16T10:00:00Z",
          lastVerifiedCommit: "abc123",
          adminNote: null,
        },
      ]),
    );
    renderWithProviders(<RepositoryDetailPage repositoryId="r1" />);

    expect(await screen.findByText("src/Web/appsettings.json")).toBeInTheDocument();
    expect(screen.getAllByText(/abc123/).length).toBeGreaterThan(0);
  });

  it("marks admin-asserted relationships differently from evidenced ones", async () => {
    server.use(
      repositoryHandler(),
      relationshipsHandler([
        {
          id: "rel2",
          sourceRepositoryId: "r1",
          targetRepositoryId: evidencedTarget,
          relationshipType: "SharesLibrary",
          status: "AdminAsserted",
          evidence: [],
          lastVerifiedAt: "2026-09-16T10:00:00Z",
          lastVerifiedCommit: null,
          adminNote: "Asserted by admin.",
        },
      ]),
    );
    renderWithProviders(<RepositoryDetailPage repositoryId="r1" />);

    expect(await screen.findByText(/asserted by an administrator/i)).toBeInTheDocument();
  });

  it("highlights a conflicted relationship and explains the conflict", async () => {
    server.use(
      repositoryHandler(),
      relationshipsHandler([
        {
          id: "rel3",
          sourceRepositoryId: "r1",
          targetRepositoryId: evidencedTarget,
          relationshipType: "ConsumesHttpApi",
          status: "Conflicted",
          adminNote: "Magnus: they talk over HTTP",
          evidence: [{ sourcePath: "src/Web/Web.csproj", commitSha: "def456", excerpt: "" }],
          lastVerifiedAt: "2026-09-16T10:00:00Z",
          lastVerifiedCommit: "def456",
        },
      ]),
    );
    renderWithProviders(<RepositoryDetailPage repositoryId="r1" />);

    const alerts = await screen.findAllByRole("alert");
    expect(alerts.some((alert) => /conflict/i.test(alert.textContent ?? ""))).toBe(true);
  });

  it("refuses to delete an evidenced relationship", async () => {
    server.use(
      repositoryHandler(),
      relationshipsHandler([
        {
          id: "rel4",
          sourceRepositoryId: "r1",
          targetRepositoryId: evidencedTarget,
          relationshipType: "SharesLibrary",
          status: "Evidenced",
          evidence: [{ sourcePath: "contracts/shared.proto", commitSha: "abc123", excerpt: "" }],
          lastVerifiedAt: "2026-09-16T10:00:00Z",
          lastVerifiedCommit: "abc123",
          adminNote: null,
        },
      ]),
    );
    renderWithProviders(<RepositoryDetailPage repositoryId="r1" />);

    expect(await screen.findByRole("button", { name: /remove/i })).toBeDisabled();
  });

  it("shows the repository profile fields", async () => {
    server.use(repositoryHandler(), relationshipsHandler([]));
    renderWithProviders(<RepositoryDetailPage repositoryId="r1" />);

    expect(await screen.findByRole("heading", { name: "orchestrator-web" })).toBeInTheDocument();
    expect(screen.getByText(baseRepository.remoteUrl)).toBeInTheDocument();
  });

  it("shows an error when the repository cannot be loaded", async () => {
    server.use(
      http.get("*/api/admin/repositories/r1", () => new HttpResponse(null, { status: 500 })),
      relationshipsHandler([]),
    );
    renderWithProviders(<RepositoryDetailPage repositoryId="r1" />);

    expect(await screen.findByRole("alert")).toHaveTextContent(/could not be loaded/i);
  });
});
