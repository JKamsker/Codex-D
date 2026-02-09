# CodexD

Standalone **HTTP + SSE** runner you can talk to from other terminals/machines.

If you’re running from source, prefix commands with:

```bash
dotnet run --project src/Codex-D.Cli/Codex-D.Cli.csproj -- <args>
```

Examples below assume a `codex-d` shim/binary is available; with `dotnet run`, insert the prefix above.

---

## HTTP mode

### Start the server

```bash
codex-d http serve
```

Defaults:
- Listens on `127.0.0.1:8787`
- Uses a project-local state dir: `<cwd>/.codex-d`
- Prints a token on startup
- When listening on non-loopback (or `--require-auth`), requests must send `Authorization: Bearer <token>`

Useful flags:
- `--listen <IP>` (default `127.0.0.1`)
- `--port <PORT>` (default `8787`)
- `--require-auth`
- `--token <TOKEN>` (overrides and persists)
- `--state-dir <DIR>`

### Start the daemon server (Windows)

```bash
codex-d http serve -d
```

Defaults:
- Windows only (fails fast on other OSes)
- Listens on `127.0.0.1` with an ephemeral port (dynamic by default)
- Uses a per-user daemon state dir under `%LOCALAPPDATA%`: `%LOCALAPPDATA%\\codex-d\\daemon\\config`
- Writes a runtime discovery file: `%LOCALAPPDATA%\\codex-d\\daemon\\config\\daemon.runtime.json`

Clients (`http exec/review/attach/ls`) prefer the daemon by default. If the daemon isn’t available, they fall back to the foreground server at the configured foreground port (default `8787`). No client command will auto-start the daemon in v1.

### Run a prompt (attached)

```bash
codex-d http exec "Hello"
```

### Run a code review (non-interactive)

Review uncommitted changes in the current repo:

```bash
codex-d http review --uncommitted
```

Review a specific commit:

```bash
codex-d http review --commit <SHA>
```

Detach (run continues on server after your terminal exits):

```bash
codex-d http exec -d "Long task"
```

Attach to a run:

```bash
codex-d http attach <RUN_ID>
```

List runs (defaults to current directory; use `--all` for everything):

```bash
codex-d http ls
codex-d http ls --all
```

Client env vars:
- `CODEX_D_URL` (explicit URL override; wins over daemon discovery)
- `CODEX_D_TOKEN`

Back-compat aliases:
- `CODEX_RUNNER_URL`
- `CODEX_RUNNER_TOKEN`

Serve env vars:
- `CODEX_D_FOREGROUND_STATE_DIR`
- `CODEX_D_DAEMON_STATE_DIR`
- `CODEX_D_FOREGROUND_PORT`
- `CODEX_D_DAEMON_PORT`

## Help

```bash
codex-d --help
codex-d http --help
```
