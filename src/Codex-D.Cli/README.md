# Codex-D.Cli (`codex-runner`)

The runner has two modes:

- `http`: standalone **HTTP + SSE** runner you can talk to from other terminals/machines.
- `cloud`: long-running runner that connects to **CodexWebUi.Api** via SignalR (the “cloud runner”).

If you’re running from source, prefix commands with:

```bash
dotnet run --project src/Codex-D.Cli/Codex-D.Cli.csproj -- <args>
```

Examples below assume a `codex-runner` shim/binary is available; with `dotnet run`, insert the prefix above.

---

## HTTP mode

### Start the server

```bash
codex-runner http serve
```

Defaults:
- Listens on `127.0.0.1:8787`
- Prints a token on startup
- When listening on non-loopback (or `--require-auth`), requests must send `Authorization: Bearer <token>`

Useful flags:
- `--listen <IP>` (default `127.0.0.1`)
- `--port <PORT>` (default `8787`)
- `--require-auth`
- `--token <TOKEN>` (overrides and persists)
- `--state-dir <DIR>`

### Run a prompt (attached)

```bash
codex-runner http exec "Hello"
```

### Run a code review (non-interactive)

Review uncommitted changes in the current repo:

```bash
codex-runner http review --uncommitted
```

Review a specific commit:

```bash
codex-runner http review --commit <SHA>
```

Detach (run continues on server after your terminal exits):

```bash
codex-runner http exec -d "Long task"
```

Attach to a run:

```bash
codex-runner http attach <RUN_ID>
```

List runs (defaults to current directory; use `--all` for everything):

```bash
codex-runner http ls
codex-runner http ls --all
```

Client env vars:
- `CODEX_RUNNER_URL` (default `http://127.0.0.1:8787`)
- `CODEX_RUNNER_TOKEN`

---

## Cloud mode

Cloud mode connects to a CodexWebUi.Api instance and waits for commands.

```bash
codex-runner cloud serve --server-url http://localhost:5000 --api-key <RUNNER_KEY>
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

---

## Help

```bash
codex-runner --help
codex-runner http --help
codex-runner cloud --help
```
