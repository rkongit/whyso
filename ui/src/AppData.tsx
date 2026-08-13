import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react'
import { api, type SessionInfo, type StatusInfo } from './api'

export interface AppData {
  status: StatusInfo | null
  sessions: SessionInfo[]
  loading: boolean
  error: string | null
  refresh: () => Promise<void>
  toast: string | null
  setToast: (msg: string | null) => void
}

const Ctx = createContext<AppData | null>(null)

function isSessionList(data: unknown): data is SessionInfo[] {
  return Array.isArray(data) && (data.length === 0 || 'promptExcerpt' in (data[0] as object) || 'id' in (data[0] as object))
}

export function AppProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<StatusInfo | null>(null)
  const [sessions, setSessions] = useState<SessionInfo[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [toast, setToast] = useState<string | null>(null)

  const refresh = useCallback(async () => {
    setLoading(true)
    try {
      const [s, list] = await Promise.all([api.status(), api.sessions()])
      setStatus(s)
      if (isSessionList(list) && (list.length === 0 || 'id' in list[0]!)) {
        // File-touch mode returns sessionId not id — only accept list rows with id.
        setSessions(
          (list as SessionInfo[]).filter((row) => typeof (row as SessionInfo).id === 'string'),
        )
      } else {
        setSessions([])
      }
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  useEffect(() => {
    if (!toast) return
    const t = window.setTimeout(() => setToast(null), 3200)
    return () => window.clearTimeout(t)
  }, [toast])

  const value = useMemo(
    () => ({ status, sessions, loading, error, refresh, toast, setToast }),
    [status, sessions, loading, error, refresh, toast],
  )

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>
}

export function useAppData(): AppData {
  const ctx = useContext(Ctx)
  if (!ctx) throw new Error('useAppData requires AppProvider')
  return ctx
}
