import { Outlet } from 'react-router-dom'
import type { ReactNode } from 'react'
import { Sidebar } from './Sidebar'
import { TopBar } from './TopBar'
import { useAppData } from '../AppData'

interface Props {
  onOpenPalette: () => void
  inspector?: ReactNode
}

export function AppShell({ onOpenPalette, inspector }: Props) {
  const { error, toast, refresh } = useAppData()
  return (
    <div className={`app-shell${inspector ? ' has-inspector' : ''}`}>
      <Sidebar />
      <TopBar onOpenPalette={onOpenPalette} onRefresh={() => void refresh()} />
      <main className="main">
        {error && <div className="error-banner">{error}</div>}
        {toast && <div className="error-banner" style={{ borderColor: '#115e59', background: '#042f2e88', color: 'var(--accent)' }}>{toast}</div>}
        <Outlet />
      </main>
      {inspector}
    </div>
  )
}
