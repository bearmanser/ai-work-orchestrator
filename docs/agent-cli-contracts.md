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

## Codex

**Status: not yet verified against the real CLI.** Task 27 (Codex provider) could not obtain a
working OpenAI API key in its build environment, so `ORCHESTRATOR_AGENT_SMOKE=1
dotnet test --filter CodexCliSmokeTests` was not run successfully — see
`tests/Orchestrator.Integration.Tests/CodexCliSmokeTests.cs`, which fails loudly with an
actionable message when the smoke flag is set but `ORCHESTRATOR_SECRET_OPENAI_API_KEY` is not,
rather than silently reporting a false pass. Whoever next runs that smoke test successfully should
replace this section with what was actually observed, and correct any of the assumptions below
that turn out to be wrong.

Everything below is the *assumed* contract, taken from the task brief and encoded in
`CodexEventStreamParser`/`CodexEventStreamParserTests` (unit-tested against recorded JSONL, not a
live CLI):

### Invocation

```json
"codex": {
  "Image": "orchestrator/worker:latest",
  "Command": ["codex"],
  "Arguments": ["exec", "--json", "--sandbox", "workspace-write", "--cd", "/work"],
  "PromptVia": "Stdin",
  "ApiKeySecretRef": "openai.api-key",
  "ApiKeyEnvironmentVariable": "OPENAI_API_KEY",
  "Model": "gpt-5-codex"
}
```

The prompt (from `PromptRenderer.Render`) is written to the process's stdin; the API key is
injected as the `OPENAI_API_KEY` environment variable inside the sandbox container.

### Output shape

Codex emits one JSON object per line on stdout (JSONL), not a single envelope. Event types
observed/assumed:

- `{"type":"session.created","session_id":"..."}` — ignored.
- `{"type":"item.completed","item":{"type":"command_execution","command":"...","exit_code":0}}`
  — becomes a `ClaimedCheck(Command, ClaimedPassed: exit_code == 0)`. This is a heuristic derived
  from the agent's own report of what it ran, not a verified check result (Task 29's runtime
  `CheckRunner` is the source of truth).
- `{"type":"item.completed","item":{"type":"agent_message","text":"..."}}` — the *last* such event
  in the stream is treated as the agent's authoritative summary (an agent revising its own summary
  mid-turn means the final one wins); currently surfaced via `AgentResult.Limitations`.
- `{"type":"turn.completed","usage":{"input_tokens":N,"output_tokens":N}}` — terminal event,
  maps to `AgentStatus.Succeeded`.
- `{"type":"turn.failed","error":{"message":"..."}}` — terminal event, maps to
  `AgentStatus.Failed`, with `FailureDetail` set from `error.message`.
- Any other `type` value (including ones this parser predates) is ignored, not fatal.
- A stream that ends without a `turn.completed` or `turn.failed` event maps to
  `AgentStatus.Partial` (the turn was cut off, e.g. by a timeout upstream).
- A line that fails to parse as JSON (e.g. a stray banner interleaved into stdout) is skipped;
  it does not abort parsing of the events around it.

Notably absent from the assumed contract: Codex's JSONL, unlike Claude's single-envelope output,
has no observed field carrying a total cost in USD, so `AgentResult.Usage.CostUsd` is currently
always `0m` for this provider — this is "cost unknown," not "this run was free," and the type
system does not yet distinguish the two (flagged by review for whoever builds cost/budget
tracking on `AgentResult`). `Usage.Duration` is not derived from the event stream either; the
provider (`CodexCliProvider`) splices in the sandbox's own measured wall-clock duration after
parsing.
