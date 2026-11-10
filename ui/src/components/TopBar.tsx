import { Monitor, Moon, RefreshCw, Search, Sun } from 'lucide-react'
import { useAppData } from '../AppData'
import { useTheme } from '../theme'

interface Props {
  onOpenPalette: () => void
  onRefresh: () => void
}

const IS_MAC = /Mac|iPhone|iPad/.test(navigator.platform)
const THEME_LABEL = { system: 'System theme', light: 'Light theme', dark: 'Dark theme' } as const

export function TopBar({ onOpenPalette, onRefresh }: Props) {
  const { status, loading } = useAppData()
  const theme = useTheme()
  const projectName = status
    ? status.root.replace(/\\/g, '/').split('/').filter(Boolean).pop() ?? status.projectId
    : '…'
  const ThemeIcon = theme.pref === 'system' ? Monitor : theme.pref === 'light' ? Sun : Moon

  return (
    <header className="topbar">
      <div className="topbar-project">
        <div className="name">{projectName}</div>
        <div className="path" title={status?.root}>
          {status?.root ?? '—'}
        </div>
      </div>

      <span
        className={`status-pill${status ? (status.daemonRunning ? ' up' : ' down') : ''}`}
        title={`Daemon ${status?.daemonStatus ?? '…'}`}
      >
        <span className="dot" />
        <span className="text">daemon {status?.daemonStatus ?? '…'}</span>
      </span>

      <div className="topbar-spacer" />

      <button
        type="button"
        className="palette-trigger"
        onClick={onOpenPalette}
        aria-label="Open command palette"
      >
        <Search size={15} />
        <span className="grow">Run a command…</span>
        <kbd>{IS_MAC ? '⌘K' : 'Ctrl K'}</kbd>
      </button>

      <button
        type="button"
        className="btn ghost icon"
        onClick={theme.cycle}
        title={`${THEME_LABEL[theme.pref]} (click to change)`}
        aria-label={`${THEME_LABEL[theme.pref]} (click to change)`}
      >
        <ThemeIcon size={16} />
      </button>

      <button
        type="button"
        className="btn ghost icon"
        onClick={onRefresh}
        disabled={loading}
        title="Refresh"
        aria-label="Refresh"
      >
        <RefreshCw size={16} className={loading ? 'spin' : undefined} />
      </button>
    </header>
  )
}
