# Worktree

Worktree is an open-source provenance layer for AI-assisted software development.
It captures the raw events produced by coding agents — prompts, tools, file
changes, commands — into a local, append-only store.

> **Phase 1** focuses on reliable raw event capture for **Cursor**.
> Higher-level analysis, UI, and multi-agent support come later.

## Download

Download the latest Windows binary from the **[Releases](https://github.com/rkongit/worktree/releases/latest)** page.

Then put `worktree.exe` on your `PATH`, or invoke it by absolute path.

## Install from source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), Git, and SQLite (bundled).

Publish a single-file binary (Windows example):

```bash
dotnet publish src/Worktree.Cli/Worktree.Cli.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o ./publish
```

Then put `./publish/worktree.exe` on your `PATH`, or invoke it by absolute path.

Linux / macOS:

```bash
dotnet publish src/Worktree.Cli/Worktree.Cli.csproj -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=true -o ./publish
# or: -r osx-arm64 / osx-x64
```

For local development you can also run without publishing:

```bash
dotnet run --project src/Worktree.Cli -- <command>
```

## Quick start

Inside any Git project:

```bash
worktree init
worktree integrate cursor
worktree daemon
```

Keep the daemon running, then use Cursor normally. Worktree records agent
activity through Cursor hooks — no manual prompt/plan/file entry.

In another terminal:

```bash
worktree status
worktree sessions
worktree sessions --file path/to/file
worktree session sess_<id>
worktree events sess_<id>
worktree activity sess_<id>
worktree tree
worktree search "auth"
worktree why commit <sha>
worktree why file <path> [--lines 10-42]
worktree rebuild
worktree ui
```

`activity` reconstructs prompt turns (paired tools/commands/files + agent plan
text). **Sessions** are the browsable unit of work (backed by rebuildable Work
Node projections). The **Work Tree** links related sessions (Git ancestry,
shared files, time, or manual `link` / `label`). `why` traces a commit or file
back to the session and prompt(s) that produced it. `sessions --file` /
`--prefix` and `search` navigate project evolution without LLM processing.
`ui` opens a localhost explorer for the same data.

## What gets stored

```text
.ai-worktree/
├── config.toml
├── worktree.db          # indexed / queryable
└── events/
    └── YYYY/MM/DD/
        └── sess_….jsonl # immutable source of truth
```

Each JSONL line is one normalized event envelope (payload stays vendor-shaped):

```json
{
  "version": "1.0",
  "event_id": "evt_…",
  "session_id": "sess_…",
  "sequence": 3,
  "timestamp": "2026-08-10T15:30:00Z",
  "agent": "cursor",
  "event_type": "file.modified",
  "payload": { "path": "src/auth/AuthService.java" }
}
```

Sessions also record `git_head` / `git_head_after` (commit SHAs at start and end).
`Worktree.Events.Normalize` can extract typed views (Prompt, ToolCall, …) from
raw events on read without changing the store.

## Architecture

```text
Cursor
  → worktree hook cursor   (adapter)
  → Normalized AgentEvent
  → worktree daemon        (POST /v1/events on localhost)
  → SQLite + JSONL
```

See [docs/architecture.md](docs/architecture.md).

## CLI

| Command | Description |
| --- | --- |
| `worktree init` | Create `.ai-worktree/` (idempotent) |
| `worktree daemon` | Start the local collector (`127.0.0.1:9477`) |
| `worktree status` | Project + daemon status |
| `worktree sessions [--file] [--prefix] [--json]` | List sessions (projections, labels, files, edges) |
| `worktree session <id> [--json]` | Inspect a session (prompts, activities, files, git) |
| `worktree events <session-id>` | List events for a session |
| `worktree activity <session-id>` | Reconstruct prompt turns for a session |
| `worktree tree [--from <id>]` | Show Work Tree edges between sessions |
| `worktree link / unlink / label` | Manual Work Tree relationships and labels |
| `worktree search <query>` | Search prompts, sessions, files, commits, activities |
| `worktree rebuild` | Rebuild session projections and auto edges |
| `worktree why commit / why file` | Provenance to session + prompt |
| `worktree ui [--port 9478] [--open]` | Localhost explorer UI (`http://127.0.0.1:9478`) |
| `worktree integrate cursor` | Install/merge `.cursor/hooks.json` |
| `worktree hook cursor` | Cursor hook entrypoint (stdin → daemon) |

## Explorer UI

The UI is a React SPA served by `worktree ui` (separate from the ingest daemon
on `:9477`). It is an IDE-style explorer for every **explore / mutate** CLI
command: status, sessions, activity, events, tree, link/unlink/label,
search, why, and rebuild. Setup commands (`init`, `daemon`, `integrate`) stay
on the CLI.

```bash
# from a project with .ai-worktree/
worktree ui
# open http://127.0.0.1:9478
```

Use the sidebar or `Ctrl/Cmd+K` command palette (each action shows its
`worktree …` equivalent). Routes: `/`, `/tree`, `/sessions`, `/why`, `/search`.

Development (API + Vite hot reload):

```bash
dotnet run --project src/Worktree.Cli -- ui --port 9478 --open false
cd ui && npm install && npm run dev   # http://127.0.0.1:5173 proxies /api → :9478
```

Production assets (copied into `src/Worktree.Ui/wwwroot`):

```bash
cd ui && npm run build
```

## Development

```bash
dotnet build Worktree.sln
dotnet test Worktree.sln
```

## License

Apache-2.0
