import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import type { ReactElement } from "react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { server } from "../../test/server";
import type { IntegrationStatus } from "../../api/types";
import { IntegrationsPage } from "./IntegrationsPage";

function renderWithProviders(ui: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>{ui}</MemoryRouter>
    </QueryClientProvider>,
  );
}

function integrationsHandler(statuses: IntegrationStatus[]) {
  return http.get("*/api/admin/integrations", () => HttpResponse.json(statuses));
}

function integration(overrides: Partial<IntegrationStatus>): IntegrationStatus {
  return {
    name: "slack",
    configured: false,
    secretFingerprint: "",
    lastCheckedAt: null,
    health: "Unknown",
    detail: null,
    ...overrides,
  };
}

describe("IntegrationsPage", () => {
  it("never renders a secret value, only a fingerprint", async () => {
    server.use(
      integrationsHandler([integration({ name: "slack", configured: true, secretFingerprint: "a1b2c3d4", health: "Healthy" })]),
    );

    renderWithProviders(<IntegrationsPage />);

    expect(await screen.findByText("a1b2c3d4")).toBeInTheDocument();
    expect(screen.queryByRole("textbox", { name: /secret value/i })).toBeNull();
  });

  it("shows connection health per integration", async () => {
    server.use(
      integrationsHandler([integration({ name: "p1", configured: true, health: "Unhealthy", detail: "timeout" })]),
    );

    renderWithProviders(<IntegrationsPage />);

    expect(await screen.findByText(/unhealthy/i)).toBeInTheDocument();
    expect(screen.getByText(/timeout/i)).toBeInTheDocument();
  });

  it("shows a message when integrations cannot be loaded", async () => {
    server.use(http.get("*/api/admin/integrations", () => HttpResponse.json({ message: "boom" }, { status: 500 })));

    renderWithProviders(<IntegrationsPage />);

    expect(await screen.findByRole("alert")).toHaveTextContent(/could not be loaded/i);
  });
});
