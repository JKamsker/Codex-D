# Codex-D HTTP Daemon + Foreground Server Plan (Iteration 1)

> Copy/paste into `tasks/` as-is.

## Goals

* **Daemon server (`http serve -d`)**

  * Runs **detached** (background) on **Windows**.
  * Uses a **state dir in `%LOCALAPPDATA%`** (global per-user).
  * Uses a **dynamic port by default** (avoid conflicts with the foreground port).
  * Writes a **runtime file** (port/baseUrl/etc.) so clients can discover it.

* **Foreground server (`http serve`)**

  * Runs in the **foreground** (current behavior).
  * Uses a **state dir relative to the current working directory** by default (project-local).

* **HTTP client operations (`http exec/review/attach/ls`)**

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

* **Daemon**: detached/background server started via `codex-d http serve -d`.
* **Foreground**: normal server started via `codex-d http serve`.

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

* `%LOCALAPPDATA%\codex-d\bin\v<version>\...`

  * Installed/copy-of-self binaries for detached running
* `%LOCALAPPDATA%\codex-d\daemon\`

  * **State directory for daemon server**:

    * `identity.json` (token + runner id)
    * `runs\...` (runs store)
    * `daemon.runtime.json` (runtime discovery file)
    * `daemon.lock` (optional; for start coordination later)
    * `daemon.log` (optional; redirect stdout/stderr)

> **Note:** Keeping daemon runtime + state co-located avoids having to manage separate “config” vs “state” in v1.

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

* `codex-d http serve`

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

* `codex-d http serve -d` (detached / daemon)

Defaults:

* `--listen 127.0.0.1`
* `--port 0` (ephemeral)
* `--state-dir %LOCALAPPDATA%\codex-d\daemon`
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

* `http serve -d` should fail fast with a clear message:

  * “Daemon mode is currently supported only on Windows. Use `codex-d http serve` (foreground) instead.”

---

## Runtime Discovery File

### File path

* `%LOCALAPPDATA%\codex-d\daemon\daemon.runtime.json`

### Schema (v1)

Write JSON (atomic: write `.tmp` then rename):

```json
{
  "baseUrl": "http://127.0.0.1:54321",
  "listen": "127.0.0.1",
  "port": 54321,
  "pid": 12345,
  "startedAtUtc": "2026-02-09T11:22:33.456Z",
  "stateDir": "C:\\Users\\me\\AppData\\Local\\codex-d\\daemon",
  "version": "1.2.3"
}
```

Notes:

* Do **not** store the token here (token already lives in `identity.json` in the same dir).
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
  codex-d http serve -d

Or start a foreground server (project-local):
  codex-d http serve
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

* `CODEX_D_FOREGROUND_STATE_DIR` (optional): default state dir for `http serve`
* `CODEX_D_DAEMON_STATE_DIR` (optional): default state dir for `http serve -d`
* `CODEX_D_FOREGROUND_PORT` (optional): default port for `http serve`
* `CODEX_D_DAEMON_PORT` (optional): default port for `http serve -d`

Command-line flags always win over env vars.

---

## Implementation Tasks Checklist

### A) Pathing / constants

* [ ] Introduce constants:

  * [ ] `DEFAULT_FOREGROUND_PORT = 8787`
  * [ ] `DEFAULT_DAEMON_PORT = 0`
  * [ ] `DEFAULT_FOREGROUND_STATE_DIR_NAME = ".codex-d"`
* [ ] Add path helpers:

  * [ ] `GetDaemonBaseDir()` → `%LOCALAPPDATA%\codex-d`
  * [ ] `GetDaemonStateDir()` → `%LOCALAPPDATA%\codex-d\daemon`
  * [ ] `GetForegroundStateDir(cwd)` → `<cwd>\.codex-d`
  * [ ] `GetDaemonRuntimeFilePath()` → `<daemonStateDir>\daemon.runtime.json`

### B) Foreground server default state dir change

* [ ] Update `http serve` default state dir to `<cwd>\.codex-d` (unless `--state-dir` / env override).
* [ ] Ensure printed banner shows the new StateDir.
* [ ] Ensure this change doesn’t affect daemon defaults.

### C) Daemon serve command (Windows detached)

* [ ] Add flag to server command:

  * [ ] `http serve -d|--daemon` (Windows-only)
* [ ] Implement “parent/child” strategy:

  * [ ] Parent spawns child process with hidden/internal `--daemon-child` flag.
  * [ ] Child runs the real Kestrel server and writes runtime file.
  * [ ] Parent exits after confirming runtime file + successful health response.
* [ ] Ensure daemon defaults:

  * [ ] `listen=127.0.0.1`
  * [ ] `port=0`
  * [ ] `stateDir=%LOCALAPPDATA%\codex-d\daemon`
  * [ ] auth required
* [ ] On non-Windows:

  * [ ] `http serve -d` prints “Windows-only” error and exits non-zero.

### D) Copy/install-self to LocalAppData (Windows)

* [ ] Determine version folder:

  * [ ] `%LOCALAPPDATA%\codex-d\bin\v<semver-or-assembly-version>\`
* [ ] Implement `InstallSelfIfNeeded()`:

  * [ ] Copy required binaries from `AppContext.BaseDirectory` into version folder.
  * [ ] Ensure correct behavior for single-file and multi-file publishing:

    * [ ] If multi-file, copy entire directory contents.
* [ ] Daemon child process should run from the installed folder (not from the original working folder).
* [ ] (Optional v1) Keep last N versions; cleanup can be v2.

### E) Daemon runtime file write

* [ ] Add runtime file writer:

  * [ ] Writes JSON atomically (`.tmp` then rename).
  * [ ] Contains baseUrl/port/listen/pid/version/stateDir/startTime.
* [ ] Must write the file **after** Kestrel binds and the final port is known.

  * [ ] If using port `0`, resolve the actual port from server addresses.

### F) Client “prefer daemon” resolution (no autostart)

* [ ] Implement a shared resolver used by exec/review/attach/ls:

  * [ ] If `--url`/env URL set → use that.
  * [ ] Else try daemon runtime file → health check → if ok use daemon.
  * [ ] Else try `http://127.0.0.1:8787` → health check → if ok use it.
  * [ ] Else print the required error message (start daemon).
* [ ] Implement token fallback:

  * [ ] If connecting to daemon and no token provided → load `<daemonStateDir>\identity.json`.
  * [ ] If connecting to foreground and server responds 401 and no token provided → load `<cwd>\.codex-d\identity.json`.

### G) Documentation updates

* [ ] Update CLI README/help text:

  * [ ] Mention daemon mode (Windows-only).
  * [ ] Explain default state dirs:

    * [ ] daemon in LocalAppData
    * [ ] foreground in `.codex-d` under CWD
  * [ ] Explain client resolution preference (daemon-first).

### H) Tests

* [ ] Update/replace `ClientSettingsBaseTests`:

  * [ ] Defaults should now be “daemon-first”, not hardcoded base URL only.
  * [ ] Add tests for:

    * [ ] `--url` overrides daemon preference
    * [ ] runtime file present + health OK selects daemon
    * [ ] daemon missing/unreachable falls back to static foreground port
    * [ ] neither available → consistent error message
* [ ] Add test utilities for faking:

  * [ ] daemon runtime file content
  * [ ] health endpoint responses (200/401/fail)

---

## Acceptance Criteria (Iteration 1)

* [ ] `codex-d http serve` starts a foreground server using `<cwd>\.codex-d` by default.
* [ ] `codex-d http serve -d` on Windows starts a detached daemon server, writes runtime file, and returns control to the terminal.
* [ ] Any client command without `--url`:

  * [ ] prefers daemon if available,
  * [ ] else tries `http://127.0.0.1:8787`,
  * [ ] else errors with “start daemon” instruction (no auto-start).
* [ ] No shared state dir between daemon and foreground by default.
* [ ] Works even if daemon requires auth (client can read token from daemon identity when not provided explicitly).

---

## Follow-ups (Iteration 2+ backlog)

* Daemon `start/stop/status` subcommands (and `/v1/shutdown` endpoint).
* Stale runtime detection using `pid` (and/or last-seen heartbeat).
* Optional “auto-start daemon” flag for client commands (explicit opt-in).
* Cross-platform daemonization (systemd/launchd) if desired later.
* More robust multi-version handling (protocol versioning + upgrade paths).
