# CodexD cloud runner (WIP)

This documentation is intentionally not linked from the main README yet.

Cloud mode connects to a CodexWebUi.Api instance and waits for commands.

```bash
codex-d cloud serve --server-url http://localhost:5000 --api-key <RUNNER_KEY>
```

Common flags:
- `--server-url <URL>` (required unless env var set)
- `--api-key <KEY>` (required unless env var set)
- `--name <NAME>` (default: hostname)
- `--identity-file <PATH>`
- `--workspace-root <PATH>` (repeatable)
- `--heartbeat-interval <DURATION>` (e.g. `5s`, `250ms`, `00:00:05`)

Env vars (same names as the legacy runner):
- `CODEXWEBUI_RUNNER_SERVER_URL`
- `CODEXWEBUI_RUNNER_API_KEY`
- `CODEXWEBUI_RUNNER_NAME`
- `CODEXWEBUI_RUNNER_IDENTITY_FILE`
- `CODEXWEBUI_RUNNER_WORKSPACE_ROOTS` (semicolon-separated)
- `CODEXWEBUI_RUNNER_HEARTBEAT_INTERVAL`
