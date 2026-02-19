# Codex-D HTTP Daemon + Foreground Server Plan (Iteration 1)

> Copy/paste into `tasks/` as-is.

## Goals

* **Daemon server (`serve -d`)**

  * Runs **detached** (background) on **Windows**.
  * Uses a **state dir in `%LOCALAPPDATA%`** (global per-user).
  * Uses a **dynamic port by default** (avoid conflicts with the foreground port).
  * Writes a **runtime file** (port/baseUrl/etc.) so clients can discover it.

* **Foreground server (`serve`)**

  * Runs in the **foreground** (current behavior).
  * Uses a **state dir relative to the current working directory** by default (project-local).

* **Client operations (`exec/review/attach/ls`)**

  * **Prefer the daemon by default.**
  * If daemon not available, **try the foreground static port**.
  * **Do not implicitly start the daemon** in this first iteration.
  * If no server is found, print a **clear instruction to start the daemon**.

## Non-goals (Iteration 1)

* No implicit daemon startup from client commands (no “auto-start”).
* No stop/status/config commands yet (optional for later).
* No multi-version handover/upgrade orchestration (just run what’s installed).
* No non-Windows daemonization (foreground server remains cross-platform).

---

## Terminology

* **Daemon**: detached/background server started via `codex-d serve -d`.
* **Foreground**: normal server started via `codex-d serve`.

---

## Defaults & Invariants

### Ports

* **Foreground port (static default):** `8787`

  * Must be **stable** and constant by default.
* **Daemon port (default):** dynamic/ephemeral (recommended: bind to port `0`).

  * Must be **different from the foreground port by default**.
  * If user explicitly sets the daemon port to `8787`, allow it (they “specified otherwise”), but warn about the consequences.

### State directory invariants

* **Daemon state dir:** under `%LOCALAPPDATA%`
* **Foreground state dir:** under current repo/project directory (relative to CWD)

**Critical invariant:** one server process should be the sole writer for its state dir (daemon and foreground use different dirs by default → no cross-process corruption).

---

## Directory & File Layout

### Daemon (Windows)

Base directory (per-user):

* `%LOCALAPPDATA%\codex-d\`

Proposed layout:

* `%LOCALAPPDATA%\codex-d\daemon\bin\`

  * Installed/copy-of-self binaries for detached running.
  * Copied from `AppContext.BaseDirectory` **only when the running version differs** from the already-installed version (version mismatch check).

* `%LOCALAPPDATA%\codex-d\daemon\config\`

  * **Config & state directory for daemon server**:

    * `identity.json` (token + runner id)
    * `runs\...` (runs store)
    * `daemon.runtime.json` (runtime discovery file)
    * `daemon.lock` (optional; for start coordination later)
    * `daemon.log` (optional; redirect stdout/stderr)

> **Note:** Keeping daemon runtime + state co-located under `config\` avoids having to manage separate directories in v1. Binaries live in a sibling `bin\` folder so they can be replaced independently on upgrade.

### Foreground (project-local)

Default state dir relative to CWD:

* `<cwd>\.codex-d\`

Contains:

* `identity.json`
* `runs\...`

---

## CLI UX & Behavior

### 1) Foreground server

Command:

* `codex-d serve`

Defaults:

* `--listen 127.0.0.1`
* `--port 8787`
* `--state-dir <cwd>\.codex-d`

Overrides:

* `--state-dir <DIR>`
* `--port <PORT>`
* `--listen <IP>`
* env override(s) (see “Environment variables”)

Behavior:

* Starts server in foreground.
* Prints:

  * BaseUrl
  * StateDir
  * Token (as today)
* Does **not** write a runtime file (not required because port is static).

### 2) Daemon server (Windows only)

Command:

* `codex-d serve -d` (detached / daemon)

Defaults:

* `--listen 127.0.0.1`
* `--port 0` (ephemeral)
* `--state-dir %LOCALAPPDATA%\codex-d\daemon\config`
* **Auth required** (even on loopback; daemon is long-lived)

Overrides:

* `--port <PORT>` (explicit)
* `--state-dir <DIR>` (explicit)
* env override(s)

Behavior (high level):

1. **Parent** command starts a **child** process running the real server.
2. Child server writes `daemon.runtime.json` after binding.
3. Parent prints the final baseUrl + guidance and exits.

**Non-Windows behavior:**

* `serve -d` should fail fast with a clear message:

  * “Daemon mode is currently supported only on Windows. Use `codex-d serve` (foreground) instead.”

---

## Runtime Discovery File

### File path

* `%LOCALAPPDATA%\codex-d\daemon\config\daemon.runtime.json`

### Schema (v1)

Write JSON (atomic: write `.tmp` then rename):

```json
{
  "baseUrl": "http://127.0.0.1:54321",
  "listen": "127.0.0.1",
  "port": 54321,
  "pid": 12345,
  "startedAtUtc": "2026-02-09T11:22:33.456Z",
  "stateDir": "C:\\Users\\me\\AppData\\Local\\codex-d\\daemon\\config",
  "version": "1.2.3"
}
```

Notes:

* Do **not** store the token here (token already lives in `identity.json` in the config dir).
* `pid` is helpful later for stale-runtime detection, but not required to rely on in v1.
* If daemon crashes and the runtime file is stale, client should treat it as “unavailable” after a failed health check.

---

## Client Resolution Rules (Exec/Review/Attach/Ls)

### Priority order for selecting server

1. If user provides `--url` (or URL env var), **use it** (explicit).
2. Otherwise:

   1. Try **daemon** first (runtime file discovery + health check)
   2. Then try **foreground** at the **static port** (`http://127.0.0.1:8787`)
3. If neither reachable: **error with “start the daemon” instruction**.

### Availability check

A server is considered “available” if:

* `GET /v1/health` returns:

  * `200 OK` (reachable), OR
  * `401 Unauthorized` (reachable but requires auth)

Connection failure / timeout / DNS error ⇒ unavailable.

### Token resolution

When building a client request:

* If user provides `--token`, use it.
* Else if token env var is set, use it.
* Else:

  * If connecting to **daemon** (discovered via daemon runtime file), attempt to load token from:

    * `<daemonStateDir>\identity.json`
  * If connecting to **foreground fallback** and auth is required (detected via initial 401), attempt to load token from:

    * `<cwd>\.codex-d\identity.json`

If token cannot be found but server requires auth:

* Fail with a message telling user how to supply a token or restart the server to print one.

---

## Required Error Message (No Implicit Start in v1)

If no server is found, every HTTP client command should produce a consistent message:

Example:

```
No running codex-d HTTP server found.

Tried:
  - Daemon: <path-to-daemon.runtime.json> (missing or unreachable)
  - Foreground: http://127.0.0.1:8787 (unreachable)

Start the daemon:
  codex-d serve -d

Or start a foreground server (project-local):
  codex-d serve
```

Important: **Do not** start anything automatically in response to this failure in v1.

---

## Environment Variables (v1)

Keep existing env vars if already present, but add daemon/foreground controls.

### Client

* `CODEX_D_URL` (optional): explicit URL override (wins over daemon preference)
* `CODEX_D_TOKEN` (optional): explicit token override

(If you need backwards compatibility, also accept current `CODEX_RUNNER_URL` / `CODEX_RUNNER_TOKEN` as aliases.)

### Serve

* `CODEX_D_FOREGROUND_STATE_DIR` (optional): default state dir for `serve`
* `CODEX_D_DAEMON_STATE_DIR` (optional): default state dir for `serve -d`
* `CODEX_D_FOREGROUND_PORT` (optional): default port for `serve`
* `CODEX_D_DAEMON_PORT` (optional): default port for `serve -d`

Command-line flags always win over env vars.

---

## Implementation Tasks Checklist

### A) Pathing / constants

* [x] Introduce constants:

  * [x] `DEFAULT_FOREGROUND_PORT = 8787`
  * [x] `DEFAULT_DAEMON_PORT = 0`
  * [x] `DEFAULT_FOREGROUND_STATE_DIR_NAME = ".codex-d"`
* [x] Add path helpers:

  * [x] `GetDaemonBaseDir()` → `%LOCALAPPDATA%\codex-d\daemon`
  * [x] `GetDaemonBinDir()` → `%LOCALAPPDATA%\codex-d\daemon\bin`
  * [x] `GetDaemonStateDir()` → `%LOCALAPPDATA%\codex-d\daemon\config`
  * [x] `GetForegroundStateDir(cwd)` → `<cwd>\.codex-d`
  * [x] `GetDaemonRuntimeFilePath()` → `<daemonStateDir>\daemon.runtime.json`

### B) Foreground server default state dir change

* [x] Update `serve` default state dir to `<cwd>\.codex-d` (unless `--state-dir` / env override).
* [x] Ensure printed banner shows the new StateDir.
* [x] Ensure this change doesn’t affect daemon defaults.

### C) Daemon serve command (Windows detached)

* [x] Add flag to server command:

  * [x] `serve -d|--daemon` (Windows-only)
* [x] Implement “parent/child” strategy:

  * [x] Parent spawns child process with hidden/internal `--daemon-child` flag.
  * [x] Child runs the real Kestrel server and writes runtime file.
  * [x] Parent exits after confirming runtime file + successful health response.
* [x] Ensure daemon defaults:

  * [x] `listen=127.0.0.1`
  * [x] `port=0`
  * [x] `stateDir=%LOCALAPPDATA%\codex-d\daemon\config`
  * [x] auth required
* [x] On non-Windows:

  * [x] `serve -d` prints “Windows-only” error and exits non-zero.

### D) Copy/install-self to daemon bin dir (Windows)

* [x] Target folder: `%LOCALAPPDATA%\codex-d\daemon\bin\`
* [x] Implement `InstallSelfIfNeeded()`:

  * [x] Compare running assembly version with a version marker in the bin dir (e.g. `.version` file).
  * [x] **Only copy if versions differ** (mismatch check).
  * [x] Copy required binaries from `AppContext.BaseDirectory` into bin folder.
  * [x] Ensure correct behavior for single-file and multi-file publishing:

    * [x] If multi-file, copy entire directory contents.
  * [x] Write/update the `.version` marker after a successful copy.
* [x] Daemon child process should run from the installed bin folder (not from the original working folder).

### E) Daemon runtime file write

* [x] Add runtime file writer:

  * [x] Writes JSON atomically (`.tmp` then rename).
  * [x] Contains baseUrl/port/listen/pid/version/stateDir/startTime.
* [x] Must write the file **after** Kestrel binds and the final port is known.

  * [x] If using port `0`, resolve the actual port from server addresses.

### F) Client “prefer daemon” resolution (no autostart)

* [x] Implement a shared resolver used by exec/review/attach/ls:

  * [x] If `--url`/env URL set → use that.
  * [x] Else try daemon runtime file → health check → if ok use daemon.
  * [x] Else try `http://127.0.0.1:8787` → health check → if ok use it.
  * [x] Else print the required error message (start daemon).
* [x] Implement token fallback:

  * [x] If connecting to daemon and no token provided → load `<daemonConfigDir>\identity.json`.
  * [x] If connecting to foreground and server responds 401 and no token provided → load `<cwd>\.codex-d\identity.json`.

### G) Documentation updates

* [x] Update CLI README/help text:

  * [x] Mention daemon mode (Windows-only).
  * [x] Explain default state dirs:

    * [x] daemon in LocalAppData
    * [x] foreground in `.codex-d` under CWD
  * [x] Explain client resolution preference (daemon-first).

### H) Tests

* [x] Update/replace `ClientSettingsBaseTests`:

  * [x] Defaults should now be “daemon-first”, not hardcoded base URL only.
  * [x] Add tests for:

    * [x] `--url` overrides daemon preference
    * [x] runtime file present + health OK selects daemon
    * [x] daemon missing/unreachable falls back to static foreground port
    * [x] neither available → consistent error message
* [x] Add test utilities for faking:

  * [x] daemon runtime file content
  * [x] health endpoint responses (200/401/fail)

---

## Acceptance Criteria (Iteration 1)

* [x] `codex-d serve` starts a foreground server using `<cwd>\.codex-d` by default.
* [x] `codex-d serve -d` on Windows starts a detached daemon server, writes runtime file, and returns control to the terminal.
* [x] Any client command without `--url`:

  * [x] prefers daemon if available,
  * [x] else tries `http://127.0.0.1:8787`,
  * [x] else errors with “start daemon” instruction (no auto-start).
* [x] No shared state dir between daemon and foreground by default.
* [x] Works even if daemon requires auth (client can read token from daemon identity when not provided explicitly).

---

## Follow-ups (Iteration 2+ backlog)

* Daemon `start/stop/status` subcommands (and `/v1/shutdown` endpoint).
* Stale runtime detection using `pid` (and/or last-seen heartbeat).
* Optional “auto-start daemon” flag for client commands (explicit opt-in).
* Cross-platform daemonization (systemd/launchd) if desired later.
* More robust multi-version handling (protocol versioning + upgrade paths).
