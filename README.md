# Whyso

Whyso is an open-source provenance layer for AI-assisted software development.
It records what your coding agents do (prompts, tool calls, file changes,
commands) into a local, append-only store, then answers **why** a commit or
line exists, and tells your next agent session what was already decided.

- **Agent-agnostic:** Cursor and Claude Code today, through each agent's hooks.
- **Safe to keep:** secrets are redacted before anything is written, and each
  session is encrypted with its own key, so `whyso forget` can erase it.
- **Local-first:** everything stays in `.whyso/` in your repo. No cloud, no LLM calls.

> Formerly **Worktree**. Renamed to avoid confusion with `git worktree`.
> Existing `.ai-worktree/` data is moved to `.whyso/` automatically on first run.

## Install

Whyso is a single self-contained binary: no runtime to install and no extra files.

**macOS / Linux**

```bash
curl -fsSL https://raw.githubusercontent.com/rkongit/whyso/main/install.sh | sh
```

**Windows (PowerShell)**

```powershell
irm https://raw.githubusercontent.com/rkongit/whyso/main/install.ps1 | iex
```

The script downloads the latest release for your platform, checks it against
`SHA256SUMS.txt`, and puts `whyso` in `~/.local/bin` (Windows:
`%LOCALAPPDATA%\Programs\whyso`, added to your user `PATH`). Set
`WHYSO_VERSION` or `WHYSO_INSTALL_DIR` to change the version or location.

**Manual:** download the archive for your platform (`win-x64`, `linux-x64`,
`osx-arm64`, `osx-x64`) from the **[Releases](https://github.com/rkongit/whyso/releases/latest)**
page, extract it, and put `whyso` (or `whyso.exe`) on your `PATH`.

> A Homebrew tap and Scoop bucket are planned
> (see [packaging/README.md](packaging/README.md)).

### From source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and Git
(SQLite is bundled).

```bash
dotnet publish src/Whyso.Cli/Whyso.Cli.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o ./publish
# other targets: -r linux-x64 | osx-arm64 | osx-x64
```

For local development, run without publishing:

```bash
dotnet run --project src/Whyso.Cli -- <command>
```

## Quick start

Inside any Git project:

```bash
whyso init
```

That is the whole setup. `init` creates `.whyso/` and installs hooks for the
agents it finds (Claude Code and/or Cursor, detected from a `.claude` /
`.cursor` folder in the project or your home directory). Then start a new agent
session and work as usual: hooks forward each event to a small local daemon,
which the first hook starts in the background. If the daemon cannot start, the
agent carries on unaffected.

To hook up an agent `init` did not detect, run `whyso integrate claude-code` or
`whyso integrate cursor`. To stop the background daemon, run
`whyso daemon stop`; it starts again on the next agent event (set
`WHYSO_NO_AUTOSTART=1` to prevent that and run `whyso daemon` yourself).

Then explore what was captured:

```bash
whyso sessions
whyso session sess_<id>
whyso activity sess_<id>
whyso why commit <sha>
whyso why file <path> [--lines 10-42]
whyso context
whyso search "auth"
whyso ui
```

`activity` reconstructs prompt turns (paired tools, commands and files, plus
the agent's response text). **Sessions** are the browsable unit of work. The
**Work Tree** links related sessions (Git ancestry, shared files, time, or
manual `link` / `label`). `why` traces a commit or file back to the session and
prompt(s) that produced it. `ui` opens a localhost explorer for the same data.

## Your agent remembers past decisions

`whyso context` prints a short brief of recent sessions: what was asked, what
was decided, and which approaches were rejected ("don't use Redis", "reverted
the ORM change"), with the files and commits involved. It is extracted
heuristically from your local history, with no LLM involved.

With the Claude Code hooks installed, the brief is injected automatically when a
Claude Code session starts, so the agent doesn't retry an approach you already
turned down. To turn that off:

```bash
whyso config set context.session_start false
```

## Privacy and erasure

- **Redaction at capture.** Before an event is written, payload strings are
  scanned for secrets (cloud and API keys, GitHub/Slack/Stripe tokens, bearer
  tokens, private keys, credentials in URLs and `password=`-style assignments,
  and long high-entropy strings). Matches become `[REDACTED:<rule>]`.
  Session ids and commit SHAs are left intact.
- **Per-session encryption.** Each session's payloads are encrypted with
  AES-256-GCM using a random key in `.whyso/keys/<session>.key`.
- **Crypto-shredding.** `whyso forget sess_<id>` deletes that key. The
  append-only log is untouched, but the session's prompts and tool payloads can
  no longer be read, and its search/summary projection is rebuilt without them.
- The search index and session summaries hold redacted plaintext so search
  works. They are rebuilt (and the old text wiped) on `forget`.
- Events captured before this release are stored unencrypted and are not
  re-encrypted.

Add `.whyso/` to your `.gitignore`: it holds your local history and the
session keys.

## Provenance in Git (opt-in)

```bash
whyso config set git.notes true
whyso daemon stop                 # it restarts with the new setting on the next event
whyso notes write                 # backfill notes for commits already captured
git push origin refs/notes/whyso  # share them
```

When a commit is linked to a session, Whyso adds a note under
`refs/notes/whyso` with the session id, prompt event id, agent and start time.
Notes contain **ids only, never prompt text**, and commit SHAs are unchanged.
In a clone that fetched the notes (`git fetch origin refs/notes/whyso:refs/notes/whyso`),
`whyso why commit <sha>` falls back to the note when there is no local capture.

## What gets stored

```text
.whyso/
├── config.toml
├── worktree.db          # indexed / queryable projection
├── keys/
│   └── sess_….key       # per-session encryption keys (delete = forget)
└── events/
    └── YYYY/MM/DD/
        └── sess_….jsonl # immutable source of truth (payloads encrypted)
```

Each JSONL line is one normalized event envelope. On disk the payload is an
encrypted `{"$enc": …}` object; readers see the redacted, vendor-shaped payload:

```json
{
  "version": "1.1",
  "event_id": "evt_…",
  "session_id": "sess_…",
  "sequence": 3,
  "timestamp": "2026-08-10T15:30:00Z",
  "agent": "claude-code",
  "event_type": "file.modified",
  "payload": { "path": "src/auth/AuthService.java" },
  "actor": { "user": "ada", "machine_id": "mach_3f9c…" },
  "project_id": "proj_…",
  "visibility": "private"
}
```

Sessions also record `git_head` / `git_head_after` (commit SHAs at start and end).

## Architecture

```text
Cursor / Claude Code
  → whyso hook cursor | claude-code   (adapters)
  → Normalized AgentEvent
  → whyso daemon                      (POST /v1/events on localhost)
  → redact → encrypt → SQLite + JSONL
```

See [docs/architecture.md](docs/architecture.md).

## CLI

| Command | Description |
| --- | --- |
| `whyso init` | Create `.whyso/` and install hooks for detected agents (idempotent; migrates `.ai-worktree/`) |
| `whyso daemon` | Run the local collector in the foreground (`127.0.0.1:9477`); hooks normally start it for you |
| `whyso daemon stop` | Stop this project's collector |
| `whyso status` | Project + daemon status |
| `whyso sessions [--file] [--prefix] [--json]` | List sessions (projections, labels, files, edges) |
| `whyso session <id> [--json]` | Inspect a session (prompts, activities, files, git) |
| `whyso events <session-id>` | List events for a session |
| `whyso activity <session-id>` | Reconstruct prompt turns for a session |
| `whyso tree [--from <id>]` | Show Work Tree edges between sessions |
| `whyso link / unlink / label` | Manual Work Tree relationships and labels |
| `whyso search <query>` | Search prompts, sessions, files, commits, activities |
| `whyso rebuild` | Rebuild session projections and auto edges |
| `whyso why commit / why file` | Provenance to session + prompt |
| `whyso context [--format md\|json]` | Brief of past decisions and rejected approaches |
| `whyso forget <session-id> [--yes]` | Delete a session's key (crypto-shred) |
| `whyso config get \| set <key> <bool>` | Settings: `context.session_start`, `git.notes` |
| `whyso notes write` | Write `refs/notes/whyso` for captured commits |
| `whyso ui [--port 9478] [--open]` | Localhost explorer UI (`http://127.0.0.1:9478`) |
| `whyso integrate cursor` | Install/merge `.cursor/hooks.json` |
| `whyso integrate claude-code` | Install/merge `.claude/settings.json` hooks |
| `whyso hook cursor \| claude-code` | Hook entrypoints (stdin → daemon) |

## Explorer UI

The UI is a React SPA served by `whyso ui` (separate from the ingest daemon
on `:9477`). It is an IDE-style explorer for every **explore / mutate** CLI
command: status, sessions, activity, events, tree, link/unlink/label,
search, why, and rebuild. Setup commands (`init`, `daemon`, `integrate`) stay
on the CLI.

```bash
# from a project with .whyso/
whyso ui
# open http://127.0.0.1:9478
```

Use the sidebar or `Ctrl/Cmd+K` command palette (each action shows its
`whyso …` equivalent). Routes: `/`, `/tree`, `/sessions`, `/why`, `/search`.

Development (API + Vite hot reload):

```bash
dotnet run --project src/Whyso.Cli -- ui --port 9478 --open false
cd ui && npm install && npm run dev   # http://127.0.0.1:5173 proxies /api → :9478
```

Production assets (built into `src/Whyso.Ui/wwwroot`, which is embedded in the
binary at build time):

```bash
cd ui && npm run build
```

## Development

```bash
dotnet build Whyso.sln
dotnet test Whyso.sln
```

## License

Apache-2.0
