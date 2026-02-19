# Repro: review detached + find rollup JSONL + stop + verify prompt/base

Goal: Start the new daemon, run a detached review (`--base` + `--prompt`), find the JSONL rollup file for that run, stop the review (not the daemon), and verify the rollup includes both the prompt text and base commit SHA.

- [ ] Build `CodexD.Cli` Release binary
- [ ] Start daemon (`codex-d daemon start`) and confirm it’s running
- [ ] Start detached review (`codex-d review --base <sha> -d --prompt "<text>"`)
- [ ] Wait ~10 seconds and confirm run is `running`
- [ ] Locate the run’s `run.json` and its `codexRolloutPath`
- [ ] Stop the review (`codex-d run stop <runId>`) and confirm it transitions to `paused`
- [ ] Verify rollup JSONL contains the exact `--prompt` text and base SHA
- [ ] If missing, fix the rollout metadata capture + add/adjust tests

