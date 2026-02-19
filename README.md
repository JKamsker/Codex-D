# CodexD

Standalone **HTTP + SSE** runner you can talk to from other terminals/machines.

If you’re running from source, prefix commands with:

```bash
dotnet run --project src/Codex-D.Cli/Codex-D.Cli.csproj -- <args>
```

Examples below assume a `codex-d` shim/binary is available; with `dotnet run`, insert the prefix above.

---

## Install (Windows)

```powershell
irm https://raw.githubusercontent.com/JKamsker/Codex-D/refs/heads/master/scripts/installer.ps1 | iex
```

Installs the app to `%LOCALAPPDATA%\codex-d\app`, then places small launchers (`codex-d.cmd`, `codex-d.ps1`, `codex-d.sh`) into an existing bin directory already on your PATH (prefers `%USERPROFILE%\.local\bin` / `%USERPROFILE%\.cargo\bin`, otherwise tries package-manager bins like `%USERPROFILE%\.dotnet\tools`). If none are found, it falls back to `%LOCALAPPDATA%\codex-d\bin` and adds it to your **user PATH**.
Open a new terminal and run `codex-d --help`.

## Install (global tool, requires .NET 10)

```bash
dotnet tool install -g CodexD
```

## Runner (HTTP/SSE)

### Start the server

```bash
codex-d serve
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
codex-d serve -d
```

Defaults:
- Windows only (fails fast on other OSes)
- Listens on `127.0.0.1` with an ephemeral port (dynamic by default)
- Uses a per-user daemon state dir under `%LOCALAPPDATA%`: `%LOCALAPPDATA%\\codex-d\\daemon\\config`
- Writes a runtime discovery file: `%LOCALAPPDATA%\\codex-d\\daemon\\config\\daemon.runtime.json`

Useful flags:
- `--force` (stop/replace any running daemon and reinstall binaries before starting)

Clients (`exec/review/attach/ls`) prefer the daemon by default. If the daemon isn’t available, they fall back to the foreground server at the configured foreground port (default `8787`). No client command will auto-start the daemon in v1.

#### Dev/debug defaults (dotnet run)

When running from source in Debug (the default `dotnet run` configuration):
- Daemon state dir defaults to `%LOCALAPPDATA%\\codex-d\\daemon-dev`
- Foreground default port is `8788` (to avoid colliding with Release’s `8787`)
- Dev daemon installs are versioned by a hash of `HEAD` + uncommitted changes; mismatches auto-replace the running dev daemon

To force dev-mode defaults in a Release build, set `CODEX_D_DEV_MODE=1`.

### Run a prompt (attached)

```bash
codex-d exec "Hello"
```

Set reasoning effort (applies to this turn and subsequent turns in the run):

```bash
codex-d exec --reasoning high "Be thorough"
```

### Run a code review (non-interactive)

Review uncommitted changes in the current repo:

```bash
codex-d review --uncommitted
```

Set reasoning effort for the review (exec mode):

```bash
codex-d review --reasoning high --uncommitted
```

Review a specific commit:

```bash
codex-d review --commit <SHA>
```

Targeted review (scope + custom instructions):

```bash
codex-d review --base <REF> --prompt "Review for parity vs 4Story…"
```

Note: upstream `codex review` (exec-mode) treats `PROMPT` as a mutually-exclusive target selector, so `codex-d` will run prompt+scope reviews via app-server mode to preserve both.

Detach (run continues on server after your terminal exits):

```bash
codex-d exec -d "Long task"
```

Attach to a run:

```bash
codex-d run attach <RUN_ID>
```

List runs (defaults to current directory; use `--all` for everything):

```bash
codex-d runs ls
codex-d runs ls --all
```

Interrupt a run:

```bash
codex-d run interrupt <RUN_ID>
codex-d run interrupt --last
```

Pause (stop) a running exec run so it can be resumed:

```bash
codex-d run stop <RUN_ID>
```

Resume a paused exec run (default prompt: "continue"):

```bash
codex-d run resume <RUN_ID>
```

Override reasoning effort on resume:

```bash
codex-d run resume --reasoning low <RUN_ID>
```

Inspect a run’s output artifacts:

```bash
codex-d run messages <RUN_ID>
codex-d run thinking <RUN_ID>
```

Show CLI + server versions (daemon/foreground):

```bash
codex-d version
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
codex-d cloud --help
```
