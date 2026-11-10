import { Outlet } from 'react-router-dom'
import { CircleAlert } from 'lucide-react'
import { Sidebar } from './Sidebar'
import { TopBar } from './TopBar'
import { Toaster } from './Toaster'
import { useAppData } from '../AppData'

interface Props {
  onOpenPalette: () => void
}

export function AppShell({ onOpenPalette }: Props) {
  const { error, refresh } = useAppData()
  return (
    <div className="app-shell">
      <Sidebar />
      <TopBar onOpenPalette={onOpenPalette} onRefresh={() => void refresh()} />
      <main className="main">
        {error && (
          <div className="banner" role="alert">
            <CircleAlert size={15} />
            <span>{error}</span>
          </div>
        )}
        <Outlet />
      </main>
      <Toaster />
    </div>
  )
}
