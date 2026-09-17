import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { EvidencePanel } from "./EvidencePanel";

describe("EvidencePanel", () => {
  it("renders local checks and P1 checks under separate regions", () => {
    render(
      <EvidencePanel
        checks={[{ name: "dotnet test", outcome: "Passed", runAt: "2026-09-17T10:00:00Z" }]}
        domainChecks={[{ name: "picked-order-count", outcome: "Passed", runAt: "2026-09-17T10:05:00Z" }]}
      />,
    );

    const local = screen.getByRole("region", { name: /local checks/i });
    const domain = screen.getByRole("region", { name: /p1 checks/i });

    expect(within(local).getByText(/dotnet test/)).toBeInTheDocument();
    expect(within(domain).getByText(/picked-order-count/)).toBeInTheDocument();
    // The two evidence kinds must never appear in the same list.
    expect(within(local).queryByText(/picked-order-count/)).toBeNull();
    expect(within(domain).queryByText(/dotnet test/)).toBeNull();
  });

  it("states when no P1 validation was performed", () => {
    render(
      <EvidencePanel
        checks={[{ name: "dotnet test", outcome: "Passed", runAt: "2026-09-17T10:00:00Z" }]}
        domainChecks={[]}
      />,
    );

    expect(screen.getByText(/no p1 validation was performed/i)).toBeInTheDocument();
  });

  it("states when no local checks have run", () => {
    render(<EvidencePanel checks={[]} domainChecks={[]} />);

    expect(screen.getByText(/no local checks have run yet/i)).toBeInTheDocument();
  });
});
