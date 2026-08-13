import { api } from './api'

export type CommandKind = 'navigate' | 'query' | 'mutate'

export interface PaletteCommand {
  id: string
  title: string
  cli: string
  kind: CommandKind
  keywords?: string[]
  /** When set, palette collects these args before running */
  args?: { name: string; placeholder: string; optional?: boolean }[]
  confirm?: string
  run: (args: Record<string, string>, helpers: CommandHelpers) => void | Promise<void>
}

export interface CommandHelpers {
  navigate: (to: string) => void
  refresh: () => Promise<void>
  toast: (msg: string) => void
  promptValues: Record<string, string>
}

export function buildCommands(): PaletteCommand[] {
  return [
    {
      id: 'nav-overview',
      title: 'Overview',
      cli: 'worktree status',
      kind: 'navigate',
      keywords: ['status', 'home'],
      run: (_a, h) => h.navigate('/'),
    },
    {
      id: 'nav-tree',
      title: 'Work Tree',
      cli: 'worktree tree',
      kind: 'navigate',
      run: (_a, h) => h.navigate('/tree'),
    },
    {
      id: 'nav-sessions',
      title: 'Sessions',
      cli: 'worktree sessions',
      kind: 'navigate',
      keywords: ['nodes', 'history'],
      run: (_a, h) => h.navigate('/sessions'),
    },
    {
      id: 'nav-why',
      title: 'Why (provenance)',
      cli: 'worktree why',
      kind: 'navigate',
      run: (_a, h) => h.navigate('/why'),
    },
    {
      id: 'nav-search',
      title: 'Search',
      cli: 'worktree search',
      kind: 'navigate',
      run: (_a, h) => h.navigate('/search'),
    },
    {
      id: 'query-search',
      title: 'Search query…',
      cli: 'worktree search <query>',
      kind: 'query',
      args: [{ name: 'q', placeholder: 'query' }],
      run: (a, h) => h.navigate(`/search?q=${encodeURIComponent(a.q ?? '')}`),
    },
    {
      id: 'query-session',
      title: 'Open session…',
      cli: 'worktree session <id>',
      kind: 'query',
      args: [{ name: 'id', placeholder: 'sess_…' }],
      run: (a, h) => h.navigate(`/sessions/${encodeURIComponent(a.id ?? '')}`),
    },
    {
      id: 'query-sessions-file',
      title: 'Sessions for file…',
      cli: 'worktree sessions --file <path>',
      kind: 'query',
      args: [{ name: 'file', placeholder: 'path/to/file' }],
      run: (a, h) => h.navigate(`/sessions?file=${encodeURIComponent(a.file ?? '')}`),
    },
    {
      id: 'query-sessions-prefix',
      title: 'Sessions for prefix…',
      cli: 'worktree sessions --prefix <dir>',
      kind: 'query',
      args: [{ name: 'prefix', placeholder: 'src/' }],
      run: (a, h) => h.navigate(`/sessions?prefix=${encodeURIComponent(a.prefix ?? '')}`),
    },
    {
      id: 'query-why-commit',
      title: 'Why commit…',
      cli: 'worktree why commit <sha>',
      kind: 'query',
      args: [{ name: 'sha', placeholder: 'commit sha' }],
      run: (a, h) => h.navigate(`/why?mode=commit&sha=${encodeURIComponent(a.sha ?? '')}`),
    },
    {
      id: 'query-why-file',
      title: 'Why file…',
      cli: 'worktree why file <path>',
      kind: 'query',
      args: [
        { name: 'path', placeholder: 'path/to/file' },
        { name: 'lines', placeholder: '10-42', optional: true },
      ],
      run: (a, h) => {
        const q = new URLSearchParams({ mode: 'file', path: a.path ?? '' })
        if (a.lines) q.set('lines', a.lines)
        h.navigate(`/why?${q}`)
      },
    },
    {
      id: 'query-tree-from',
      title: 'Tree focused on session…',
      cli: 'worktree tree --from <id>',
      kind: 'query',
      args: [{ name: 'from', placeholder: 'sess_…' }],
      run: (a, h) => h.navigate(`/tree?from=${encodeURIComponent(a.from ?? '')}`),
    },
    {
      id: 'mutate-link',
      title: 'Link sessions…',
      cli: 'worktree link <from> <to> [--label]',
      kind: 'mutate',
      args: [
        { name: 'from', placeholder: 'from sess_…' },
        { name: 'to', placeholder: 'to sess_…' },
        { name: 'label', placeholder: 'feature|fix|…', optional: true },
      ],
      run: async (a, h) => {
        await api.link(a.from!, a.to!, a.label || undefined)
        await h.refresh()
        h.toast(`Linked ${a.from} → ${a.to}`)
        h.navigate('/tree')
      },
    },
    {
      id: 'mutate-unlink',
      title: 'Unlink sessions…',
      cli: 'worktree unlink <from> <to>',
      kind: 'mutate',
      args: [
        { name: 'from', placeholder: 'from sess_…' },
        { name: 'to', placeholder: 'to sess_…' },
        { name: 'kind', placeholder: 'manual', optional: true },
      ],
      run: async (a, h) => {
        await api.unlink(a.from!, a.to!, a.kind || 'manual')
        await h.refresh()
        h.toast(`Unlinked ${a.from} → ${a.to}`)
        h.navigate('/tree')
      },
    },
    {
      id: 'mutate-label',
      title: 'Label session…',
      cli: 'worktree label <id> <label>',
      kind: 'mutate',
      args: [
        { name: 'id', placeholder: 'sess_…' },
        { name: 'label', placeholder: 'feature|fix|refactor' },
      ],
      run: async (a, h) => {
        await api.addLabel(a.id!, a.label!)
        await h.refresh()
        h.toast(`Labeled ${a.id} as ${a.label}`)
        h.navigate(`/sessions/${encodeURIComponent(a.id!)}`)
      },
    },
    {
      id: 'mutate-unlabel',
      title: 'Remove label…',
      cli: 'worktree label <id> <label> --remove',
      kind: 'mutate',
      args: [
        { name: 'id', placeholder: 'sess_…' },
        { name: 'label', placeholder: 'label' },
      ],
      run: async (a, h) => {
        await api.removeLabel(a.id!, a.label!)
        await h.refresh()
        h.toast(`Removed label ${a.label} from ${a.id}`)
        h.navigate(`/sessions/${encodeURIComponent(a.id!)}`)
      },
    },
    {
      id: 'mutate-rebuild',
      title: 'Rebuild projections',
      cli: 'worktree rebuild',
      kind: 'mutate',
      confirm: 'Rebuild all session projections and auto edges?',
      run: async (_a, h) => {
        const r = await api.rebuild()
        await h.refresh()
        h.toast(`Rebuilt ${r.nodeCount} sessions`)
        h.navigate('/')
      },
    },
  ]
}

export function filterCommands(commands: PaletteCommand[], query: string): PaletteCommand[] {
  const q = query.trim().toLowerCase()
  if (!q) return commands
  return commands.filter((c) => {
    const hay = [c.title, c.cli, c.id, ...(c.keywords ?? [])].join(' ').toLowerCase()
    return hay.includes(q) || q.split(/\s+/).every((t) => hay.includes(t))
  })
}
