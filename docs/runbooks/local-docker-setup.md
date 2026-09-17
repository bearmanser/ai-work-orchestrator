# Local Docker setup for this session

No `docker` CLI exists on the Windows PATH here. The daemon runs inside WSL2 and is reached over TCP. Do this at the start of every session before running any test that touches Postgres or the agent sandbox.

## 1. Keep the WSL distro alive

WSL shuts the distro down seconds after the last `wsl.exe` call exits, which kills the daemon with it. Hold it open for the whole session:

```bash
wsl.exe -e sleep 28800   # run with run_in_background: true, do not wait on it
```

Re-run this any time Docker becomes unreachable (see step 2) — it means the keep-alive died.

## 2. Verify the daemon is reachable from Windows

`curl` is blocked in this shell and its failure looks identical to a network error — never use it to check Docker. Use PowerShell instead:

```powershell
Invoke-RestMethod -Uri 'http://127.0.0.1:2375/version' -TimeoutSec 5
```

If that fails, redo step 1.

## 3. Set `DOCKER_HOST` for every test/build command

```bash
export DOCKER_HOST=tcp://127.0.0.1:2375
dotnet test Orchestrator.sln --configuration Debug -v minimal
```

Testcontainers and EF both talk the Engine API directly over this and need no CLI.

## 4. Invoking the `docker` CLI itself

`SandboxedProcessRunner` (Task 25) shells out to a literal `docker` command as its security boundary. Since there's no native CLI, the invoked executable is configurable via `Sandbox:DockerCommand`, overridden in `src/Orchestrator.Api/appsettings.Development.json` to:

```json
["wsl.exe", "-e", "env", "DOCKER_HOST=tcp://127.0.0.1:2375", "docker"]
```

Production (Linux) uses the default `["docker"]`. Never hardcode `"docker"` in new code — always go through `SandboxOptions`/DI.

## 5. Gotchas already hit once — don't repeat them

- `IConfiguration.Bind()` **merges** into the default `DockerCommand` list instead of replacing it. Set the override value directly, not via `Bind()`.
- Clearing all env vars before launching `wsl.exe` breaks the launch itself — keep `SystemRoot`.
- `dotnet ef migrations add` writes UTF-8-BOM/CRLF on Windows regardless of `.gitattributes`. Run `dotnet format` or `git add --renormalize .` after generating a migration and confirm `git diff` shows no unrelated whole-file rewrites before committing.
