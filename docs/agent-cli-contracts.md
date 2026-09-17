# Agent CLI contracts

This file pins the observed surface of each coding-agent CLI the orchestrator drives, since it is
the one place the plan depends on an external tool's behavior rather than something this repo
controls. Each provider owns its own section below; add a new section rather than editing another
provider's.

## Claude

**Status:** not exercised against the real CLI in this environment. `ORCHESTRATOR_AGENT_SMOKE=1`
was set and the sandbox infrastructure came up, but no working `anthropic.api-key` secret was
available (no `ANTHROPIC_API_KEY`/`ORCHESTRATOR_SECRET_ANTHROPIC_API_KEY` and no file-based secret
configured), so `ClaudeCliSmokeTests` short-circuited without invoking `claude`. The configuration
and parser below are therefore based on the task brief's documented envelope shape and the
`claude -p --output-format json` contract as publicly documented, not on a first-hand observation
from this machine. Whoever next has a working key should run the smoke test and update this
section with what was actually observed, correcting anything below that turns out to be wrong.

**Configuration** (`AgentProviders:claude` in `src/Orchestrator.Api/appsettings.json`):

```json
{
  "Image": "orchestrator/worker:latest",
  "Command": ["claude"],
  "Arguments": ["-p", "--output-format", "json", "--permission-mode", "acceptEdits"],
  "PromptVia": "Stdin",
  "ApiKeySecretRef": "anthropic.api-key",
  "ApiKeyEnvironmentVariable": "ANTHROPIC_API_KEY",
  "Model": "claude-opus-5"
}
```

**Result envelope** (assumed shape, per the task brief):

```json
{
  "type": "result",
  "subtype": "success",
  "is_error": false,
  "result": "free-text summary of what the agent did",
  "session_id": "sess-1",
  "total_cost_usd": 0.4213,
  "num_turns": 9,
  "duration_ms": 84000,
  "usage": { "input_tokens": 120000, "output_tokens": 4200 }
}
```

`subtype: "success"` maps to `AgentStatus.Succeeded`; `subtype: "error_max_turns"` maps to
`AgentStatus.Partial`; any other envelope with `is_error: true` maps to `AgentStatus.Failed`, with
`FailureDetail` set from `result`.

`Orchestrator.Adapters.Agent.Providers.ClaudeResultParser` scans backward from the last `{` in the
raw stdout for the first position that opens a JSON object balanced all the way to the end of the
text, so leading noise (e.g. an npm version-check banner) before the real envelope does not break
parsing.

Claimed checks (`AgentResult.ClaimedChecks`) are produced by a fixed heuristic command list
(`dotnet test`, `dotnet build`, `npm test`, etc.) matched against the free-text `result` field -
this is only ever a hint to an operator about what the agent said it ran, never verified evidence.
