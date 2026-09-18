export class ApiError extends Error {
  readonly status: number;
  readonly detail: string;

  constructor(status: number, detail: string) {
    super(detail);
    this.name = "ApiError";
    this.status = status;
    this.detail = detail;
  }
}

const CSRF_COOKIE = "orchestrator_csrf";
const UNSAFE_METHODS = new Set(["POST", "PUT", "PATCH", "DELETE"]);

export const unauthorizedEvent = "orchestrator:unauthorized";

export function readCookie(name: string): string | null {
  if (typeof document === "undefined") return null;
  const prefix = `${name}=`;
  for (const part of document.cookie.split(";")) {
    const trimmed = part.trim();
    if (trimmed.startsWith(prefix)) {
      return decodeURIComponent(trimmed.slice(prefix.length));
    }
  }
  return null;
}

function resolveUrl(path: string): string {
  if (/^https?:\/\//i.test(path)) return path;
  // Absolute same-origin URL so the client also works under Node's fetch
  // (undici), which rejects bare relative paths in tests.
  if (typeof window !== "undefined" && window.location?.origin) {
    return new URL(path, window.location.origin).toString();
  }
  return path;
}

async function request<T>(
  method: string,
  path: string,
  body?: unknown,
  extraHeaders?: Record<string, string>,
): Promise<T> {
  const headers: Record<string, string> = { ...extraHeaders };
  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
  }
  if (UNSAFE_METHODS.has(method.toUpperCase())) {
    const token = readCookie(CSRF_COOKIE);
    if (token !== null) {
      headers["X-CSRF-Token"] = token;
    }
  }

  const response = await fetch(resolveUrl(path), {
    method,
    credentials: "include",
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  if (response.status === 401) {
    if (typeof window !== "undefined") {
      window.dispatchEvent(new CustomEvent(unauthorizedEvent));
      // No hard navigation here: the anonymous session check itself 401s
      // (including on /login), so navigating would reload into an infinite
      // loop. Expired sessions bounce to /login through the router instead
      // (see RequireSession reacting to the event above via useSession).
    }
    throw new ApiError(401, "Unauthorized");
  }

  if (!response.ok) {
    throw new ApiError(response.status, await readDetail(response));
  }

  if (response.status === 204 || response.status === 205) {
    return undefined as T;
  }
  const text = await response.text();
  if (text.length === 0) {
    return undefined as T;
  }
  return JSON.parse(text) as T;
}

async function readDetail(response: Response): Promise<string> {
  try {
    const data = (await response.json()) as { detail?: unknown; message?: unknown };
    if (typeof data.detail === "string" && data.detail.length > 0) return data.detail;
    if (typeof data.message === "string" && data.message.length > 0) return data.message;
  } catch {
    // Fall through to statusText below.
  }
  return response.statusText || `Request failed with status ${response.status}`;
}

export const apiClient = {
  get: <T>(path: string): Promise<T> => request<T>("GET", path),
  post: <T>(path: string, body?: unknown): Promise<T> => request<T>("POST", path, body),
  // `headers` lets callers attach things like `If-Match` for optimistic-concurrency
  // updates (see RepositoryEndpoints.Update) without every PUT needing to know about it.
  put: <T>(path: string, body?: unknown, headers?: Record<string, string>): Promise<T> =>
    request<T>("PUT", path, body, headers),
  patch: <T>(path: string, body?: unknown): Promise<T> => request<T>("PATCH", path, body),
  del: <T>(path: string): Promise<T> => request<T>("DELETE", path),
};
