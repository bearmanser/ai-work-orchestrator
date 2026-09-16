// admin/src/features/auth/LoginPage.test.tsx
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { LoginPage } from "./LoginPage";

describe("LoginPage", () => {
  it("submits the login name and password", async () => {
    const login = vi.fn().mockResolvedValue(undefined);
    render(<LoginPage onLogin={login} />);

    await userEvent.type(screen.getByLabelText(/login name/i), "magnus");
    await userEvent.type(screen.getByLabelText(/password/i), "a-long-enough-passphrase");
    await userEvent.click(screen.getByRole("button", { name: /sign in/i }));

    expect(login).toHaveBeenCalledWith("magnus", "a-long-enough-passphrase");
  });

  it("shows an error when the credentials are rejected", async () => {
    const login = vi.fn().mockRejectedValue({ status: 401, detail: "Invalid credentials" });
    render(<LoginPage onLogin={login} />);

    await userEvent.type(screen.getByLabelText(/login name/i), "magnus");
    await userEvent.type(screen.getByLabelText(/password/i), "wrong");
    await userEvent.click(screen.getByRole("button", { name: /sign in/i }));

    expect(await screen.findByRole("alert")).toHaveTextContent(/invalid credentials/i);
  });

  it("offers no way to create an account", () => {
    render(<LoginPage onLogin={vi.fn()} />);
    expect(screen.queryByRole("link", { name: /sign up|register|create account/i })).toBeNull();
  });

  it("does not keep the password in the DOM after submitting", async () => {
    render(<LoginPage onLogin={vi.fn().mockResolvedValue(undefined)} />);
    await userEvent.type(screen.getByLabelText(/password/i), "a-long-enough-passphrase");
    await userEvent.click(screen.getByRole("button", { name: /sign in/i }));

    expect(document.body.innerHTML).not.toContain("a-long-enough-passphrase");
  });
});
