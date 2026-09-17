import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { RepositoryRelationship } from "../../api/types";
import { RelationshipGraph } from "./RelationshipGraph";

const evidenced: RepositoryRelationship = {
  id: "rel1",
  sourceRepositoryId: "r1",
  targetRepositoryId: "r2",
  relationshipType: "ConsumesHttpApi",
  status: "Evidenced",
  evidence: [{ sourcePath: "src/Web/appsettings.json", commitSha: "abc123", excerpt: "" }],
  lastVerifiedAt: "2026-09-16T10:00:00Z",
  lastVerifiedCommit: "abc123",
  adminNote: null,
};

describe("RelationshipGraph", () => {
  it("shows a message when there are no relationships", () => {
    render(<RelationshipGraph relationships={[]} onRemove={vi.fn()} />);
    expect(screen.getByText(/no relationships have been recorded/i)).toBeInTheDocument();
  });

  it("enables removal for admin-asserted relationships and calls onRemove", async () => {
    const onRemove = vi.fn();
    render(
      <RelationshipGraph
        relationships={[{ ...evidenced, id: "rel2", status: "AdminAsserted", evidence: [] }]}
        onRemove={onRemove}
      />,
    );

    const button = screen.getByRole("button", { name: /remove/i });
    expect(button).toBeEnabled();
    await userEvent.click(button);
    expect(onRemove).toHaveBeenCalledWith("rel2");
  });

  it("disables removal for evidenced relationships with an explanatory title", () => {
    render(<RelationshipGraph relationships={[evidenced]} onRemove={vi.fn()} />);

    const button = screen.getByRole("button", { name: /remove/i });
    expect(button).toBeDisabled();
    expect(button.getAttribute("title")).toMatch(/cannot be removed/i);
  });
});
