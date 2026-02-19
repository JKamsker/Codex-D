# Uniform CLI + Daemon/Server Refactor (Tasklist)

Goal: make the `codex-d` CLI mostly uniform across run kinds (exec vs review/appserver), and refactor HTTP runner startup/shutdown into `daemon`/`server` command groups.

## Checklist

- [x] Add this tasklist file.

### Uniform run lifecycle

- [x] Make `run stop` pause *any* run kind (review/appserver included) and emit `run.paused`.
- [x] Make `run resume` work for `kind=review` by continuing as an exec/appserver turn with a continuation prefix.
- [x] Update CLI help + README text for the new uniform semantics.
- [x] Add/adjust tests for stop/resume on `kind=review`.

### Daemon/Server command groups

- [x] Add `codex-d daemon start|stop|status` and `codex-d server start|stop|status` command groups.
- [x] Make `codex-d serve` foreground-only; `codex-d serve -d/--daemon` becomes a hard error pointing to `codex-d daemon start`.
- [x] Add `POST /v1/shutdown` to the HTTP runner server (auth-protected) for graceful shutdown.
- [x] Implement `codex-d daemon stop` via `/v1/shutdown` (with optional `--force` fallback kill).
- [ ] Ensure daemon runtime file cleanup on shutdown and update docs/help hints (`codex-d status`, README).
