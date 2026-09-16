import { useState } from "react";
import type { FormEvent } from "react";

export interface LoginPageProps {
  onLogin: (loginName: string, password: string) => Promise<void>;
}

function errorDetail(error: unknown): string {
  if (typeof error === "object" && error !== null && "detail" in error) {
    const detail = (error as { detail?: unknown }).detail;
    if (typeof detail === "string" && detail.length > 0) return detail;
  }
  if (error instanceof Error && error.message.length > 0) return error.message;
  return "Sign in failed.";
}

export function LoginPage({ onLogin }: LoginPageProps) {
  const [loginName, setLoginName] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const name = loginName;
    const secret = password;
    // Clear the password from component state (and therefore the DOM)
    // before the request resolves so it is never retained on screen.
    setPassword("");
    setError(null);
    try {
      await onLogin(name, secret);
    } catch (err) {
      setError(errorDetail(err));
    }
  }

  return (
    <main>
      <h1>Sign in</h1>
      <form onSubmit={handleSubmit}>
        <div>
          <label htmlFor="login-name">Login name</label>
          <input
            id="login-name"
            name="loginName"
            type="text"
            autoComplete="username"
            value={loginName}
            onChange={(event) => setLoginName(event.target.value)}
          />
        </div>
        <div>
          <label htmlFor="password">Password</label>
          <input
            id="password"
            name="password"
            type="password"
            autoComplete="current-password"
            value={password}
            onChange={(event) => setPassword(event.target.value)}
          />
        </div>
        {error !== null && <p role="alert">{error}</p>}
        <button type="submit">Sign in</button>
      </form>
    </main>
  );
}
