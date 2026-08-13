import { useAppData } from '../AppData'

interface Props {
  onOpenPalette: () => void
  onRefresh: () => void
}

export function TopBar({ onOpenPalette, onRefresh }: Props) {
  const { status } = useAppData()
  const projectName = status
    ? status.root.replace(/\\/g, '/').split('/').filter(Boolean).pop() ?? status.projectId
    : '…'

  return (
    <header className="topbar">
      <div className="topbar-project">
        <div className="name">{projectName}</div>
        <div className="path">{status?.root ?? '—'}</div>
      </div>

      <span className={`status-pill${status?.daemonRunning ? ' up' : ' down'}`}>
        <span className="dot" />
        daemon {status?.daemonStatus ?? '…'}
      </span>

      <div className="topbar-spacer" />

      <button type="button" className="palette-trigger" onClick={onOpenPalette}>
        <span>Run a command…</span>
        <kbd>Ctrl K</kbd>
      </button>

      <button type="button" className="btn" onClick={onRefresh} title="Refresh">
        Refresh
      </button>
    </header>
  )
}
