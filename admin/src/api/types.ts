// Mirrors the server DTOs by hand. Keep the names identical to the C# records
// in src/Orchestrator.Api (e.g. LoginRequest in Auth/AuthEndpoints.cs) and
// update this file whenever an endpoint changes shape.

/** POST /api/auth/login body — mirrors AuthEndpoints.LoginRequest. */
export interface LoginRequest {
  loginName: string;
  password: string;
}

/** GET /api/auth/me response — mirrors the anonymous { loginName, isAdministrator } result. */
export interface SessionInfo {
  loginName: string;
  isAdministrator: boolean;
}
