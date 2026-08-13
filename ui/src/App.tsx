import { useEffect, useState } from 'react'
import { BrowserRouter, Navigate, Route, Routes, useParams } from 'react-router-dom'
import { AppProvider } from './AppData'
import { AppShell } from './components/AppShell'
import { CommandPalette } from './components/CommandPalette'
import { OverviewView } from './views/OverviewView'
import { TreeView } from './views/TreeView'
import { SessionsView } from './views/SessionsView'
import { WhyView } from './views/WhyView'
import { SearchView } from './views/SearchView'
import './index.css'

function RedirectNodeToSession() {
  const { id } = useParams()
  return <Navigate to={id ? `/sessions/${encodeURIComponent(id)}` : '/sessions'} replace />
}

function ShellRoutes() {
  const [paletteOpen, setPaletteOpen] = useState(false)

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault()
        setPaletteOpen((o) => !o)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  return (
    <>
      <Routes>
        <Route element={<AppShell onOpenPalette={() => setPaletteOpen(true)} />}>
          <Route index element={<OverviewView />} />
          <Route path="tree" element={<TreeView />} />
          <Route path="sessions" element={<SessionsView />} />
          <Route path="sessions/:id" element={<SessionsView />} />
          <Route path="why" element={<WhyView />} />
          <Route path="search" element={<SearchView />} />
          <Route path="nodes" element={<Navigate to="/sessions" replace />} />
          <Route path="nodes/:id" element={<RedirectNodeToSession />} />
          <Route path="history" element={<Navigate to="/sessions" replace />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Routes>
      <CommandPalette open={paletteOpen} onClose={() => setPaletteOpen(false)} />
    </>
  )
}

export default function App() {
  return (
    <BrowserRouter>
      <AppProvider>
        <ShellRoutes />
      </AppProvider>
    </BrowserRouter>
  )
}
