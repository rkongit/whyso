import { useEffect, useMemo, useState } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { api, type FileTouch, type SessionDetail, type SessionInfo } from '../api'
import { useAppData } from '../AppData'
import {
  basename,
  formatDay,
  formatDuration,
  formatTime,
  shortSha,
  titleFromExcerpt,
} from '../format'

type Tab =
  | 'summary'
  | 'activity'
  | 'files'
  | 'tests'
  | 'commands'
  | 'git'
  | 'events'

function isFileTouchList(data: SessionInfo[] | FileTouch[]): data is FileTouch[] {
  return data.length > 0 && 'sessionId' in data[0]! && !('id' in data[0]!)
}

export function SessionsView() {
  const { id } = useParams()
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()
  const { sessions: appSessions, refresh } = useAppData()
  const file = params.get('file') ?? ''
  const prefix = params.get('prefix') ?? ''
  const [filter, setFilter] = useState('')
  const [fileInput, setFileInput] = useState(file)
  const [prefixInput, setPrefixInput] = useState(prefix)
  const [list, setList] = useState<SessionInfo[]>(appSessions)
  const [fileTouches, setFileTouches] = useState<FileTouch[] | null>(null)
  const [listError, setListError] = useState<string | null>(null)

  useEffect(() => {
    setFileInput(file)
    setPrefixInput(prefix)
  }, [file, prefix])

  useEffect(() => {
    if (!file && !prefix) {
      setList(appSessions)
      setFileTouches(null)
      return
    }
    let cancelled = false
    api
      .sessions({ file: file || undefined, prefix: prefix || undefined })
      .then((data) => {
        if (cancelled) return
        if (file && !prefix && isFileTouchList(data)) {
          setFileTouches(data)
          setList([])
        } else {
          setFileTouches(null)
          setList(data as SessionInfo[])
        }
        setListError(null)
      })
      .catch((e: Error) => {
        if (!cancelled) setListError(e.message)
      })
    return () => {
      cancelled = true
    }
  }, [file, prefix, appSessions])

  const filtered = useMemo(() => {
    const q = filter.trim().toLowerCase()
    if (!q) return list
    return list.filter(
      (n) =>
        n.id.toLowerCase().includes(q) ||
        (n.promptExcerpt ?? '').toLowerCase().includes(q) ||
        (n.labels ?? []).some((l) => l.toLowerCase().includes(q)),
    )
  }, [list, filter])

  function applyFilters() {
    const next = new URLSearchParams()
    if (fileInput.trim()) next.set('file', fileInput.trim())
    if (prefixInput.trim()) next.set('prefix', prefixInput.trim())
    if (id) {
      navigate(`/sessions/${encodeURIComponent(id)}${next.toString() ? `?${next}` : ''}`)
    } else {
      setParams(next)
    }
  }

  return (
    <div className="page full">
      <div className="split">
        <div className="split-list">
          <div style={{ padding: 10, borderBottom: '1px solid var(--border)' }}>
            <div style={{ fontWeight: 600, marginBottom: 6 }}>Sessions</div>
            <input
              className="inline-input"
              style={{ width: '100%', marginBottom: 6 }}
              placeholder="Filter…"
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
            />
            <input
              className="inline-input"
              style={{ width: '100%', marginBottom: 6 }}
              placeholder="--file path"
              value={fileInput}
              onChange={(e) => setFileInput(e.target.value)}
            />
            <input
              className="inline-input"
              style={{ width: '100%', marginBottom: 6 }}
              placeholder="--prefix src/"
              value={prefixInput}
              onChange={(e) => setPrefixInput(e.target.value)}
            />
            <button type="button" className="btn primary" style={{ width: '100%' }} onClick={applyFilters}>
              Apply filters
            </button>
            <div className="cli-hint">whyso sessions</div>
          </div>
          {listError && <div className="error-banner">{listError}</div>}
          {fileTouches ? (
            fileTouches.map((t, i) => (
              <button
                key={`${t.sessionId}-${t.at}-${i}`}
                type="button"
                className={`list-item${id === t.sessionId ? ' active' : ''}`}
                onClick={() => navigate(`/sessions/${encodeURIComponent(t.sessionId)}`)}
              >
                <div className="title">{titleFromExcerpt(t.promptExcerpt)}</div>
                <div className="meta">
                  {formatDay(t.at)} · {t.action} · {basename(t.path)}
                </div>
              </button>
            ))
          ) : (
            filtered.map((n) => (
              <button
                key={n.id}
                type="button"
                className={`list-item${id === n.id ? ' active' : ''}`}
                onClick={() => navigate(`/sessions/${encodeURIComponent(n.id)}`)}
              >
                <div className="title">{titleFromExcerpt(n.promptExcerpt)}</div>
                <div className="meta">
                  {formatDay(n.startedAt)} · {n.status} · {n.fileCount}f
                  {n.incomingEdges != null ? ` · e ${n.incomingEdges}/${n.outgoingEdges}` : ''}
                </div>
                {(n.labels?.length ?? 0) > 0 && (
                  <div style={{ marginTop: 4 }}>
                    {n.labels!.map((l) => (
                      <span key={l} className="badge accent">
                        {l}
                      </span>
                    ))}
                  </div>
                )}
              </button>
            ))
          )}
          {!fileTouches && filtered.length === 0 && (
            <div className="empty">
              No sessions
              <br />
              <code>whyso rebuild</code>
            </div>
          )}
        </div>
        <div className="split-detail">
          {id ? (
            <SessionDetailView sessionId={id} onChanged={() => void refresh()} />
          ) : (
            <div className="empty">
              Select a session
              <br />
              <code>whyso session &lt;id&gt;</code>
            </div>
          )}
        </div>
      </div>
    </div>
  )
}

export function SessionDetailView({
  sessionId,
  onChanged,
}: {
  sessionId: string
  onChanged?: () => void
}) {
  const [detail, setDetail] = useState<SessionDetail | null>(null)
  const [events, setEvents] = useState<unknown[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [tab, setTab] = useState<Tab>('summary')
  const [label, setLabel] = useState('')
  const { refresh, setToast } = useAppData()

  useEffect(() => {
    let cancelled = false
    setDetail(null)
    setEvents(null)
    setError(null)
    api
      .session(sessionId)
      .then((d) => {
        if (!cancelled) setDetail(d)
      })
      .catch((e: Error) => {
        if (!cancelled) setError(e.message)
      })
    return () => {
      cancelled = true
    }
  }, [sessionId])

  useEffect(() => {
    if (tab !== 'events') return
    let cancelled = false
    api
      .events(sessionId)
      .then((e) => {
        if (!cancelled) setEvents(e)
      })
      .catch((e: Error) => {
        if (!cancelled) setError(e.message)
      })
    return () => {
      cancelled = true
    }
  }, [tab, sessionId])

  if (error) return <div className="error-banner">{error}</div>
  if (!detail) return <div className="empty">Loading session…</div>

  const tabs: { id: Tab; label: string }[] = [
    { id: 'summary', label: 'Summary' },
    { id: 'activity', label: 'Activity' },
    { id: 'files', label: 'Files' },
    { id: 'tests', label: 'Tests' },
    { id: 'commands', label: 'Commands' },
    { id: 'git', label: 'Git' },
    { id: 'events', label: 'Events' },
  ]

  return (
    <div>
      <div style={{ padding: '14px 16px', borderBottom: '1px solid var(--border)' }}>
        <h1 style={{ margin: 0, fontSize: 16 }}>{titleFromExcerpt(detail.prompts[0])}</h1>
        <div className="mono dim" style={{ marginTop: 4 }}>
          {detail.id}
        </div>
        <div style={{ marginTop: 8, display: 'flex', flexWrap: 'wrap', gap: 6, alignItems: 'center' }}>
          <span className={`badge ${detail.status === 'active' ? 'warn' : 'ok'}`}>{detail.status}</span>
          <span className="badge">{detail.agent}</span>
          <span className="badge">{formatDuration(detail.durationSeconds)}</span>
          {detail.labels.map((l) => (
            <span key={l} className="label-chip">
              {l}
              <button
                type="button"
                onClick={() =>
                  void (async () => {
                    await api.removeLabel(sessionId, l)
                    const d = await api.session(sessionId)
                    setDetail(d)
                    await refresh()
                    onChanged?.()
                    setToast(`Removed ${l}`)
                  })()
                }
              >
                ×
              </button>
            </span>
          ))}
          <input
            className="inline-input"
            style={{ width: 120 }}
            placeholder="add label"
            value={label}
            onChange={(e) => setLabel(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && label.trim()) {
                void (async () => {
                  await api.addLabel(sessionId, label.trim())
                  const d = await api.session(sessionId)
                  setDetail(d)
                  await refresh()
                  onChanged?.()
                  setToast(`Labeled ${label.trim()}`)
                  setLabel('')
                })()
              }
            }}
          />
        </div>
        <div className="cli-hint">
          whyso session {sessionId} · whyso activity {sessionId} · whyso events {sessionId}
        </div>
      </div>

      <div className="tabs">
        {tabs.map((t) => (
          <button
            key={t.id}
            type="button"
            className={`tab${tab === t.id ? ' active' : ''}`}
            onClick={() => setTab(t.id)}
          >
            {t.label}
          </button>
        ))}
      </div>

      <div className="detail-body">
        {tab === 'summary' && (
          <div className="stack">
            <div className="block">
              <h3>First prompt</h3>
              <pre className="pre">{detail.prompts[0] || '—'}</pre>
            </div>
            {detail.responses[0] && (
              <div className="block">
                <h3>Last response</h3>
                <pre className="pre">{detail.responses[detail.responses.length - 1]}</pre>
              </div>
            )}
            <div className="block">
              <h3>Afterwards</h3>
              {detail.afterwards.length === 0 && <div className="dim">—</div>}
              {detail.afterwards.map((a) => (
                <div key={a.id} style={{ marginBottom: 6 }}>
                  <Link to={`/sessions/${encodeURIComponent(a.id)}`}>
                    {titleFromExcerpt(a.promptExcerpt)}
                  </Link>
                  <div className="dim mono">
                    via {a.via} · {formatTime(a.startedAt)}
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}

        {tab === 'activity' && (
          <div className="stack">
            {detail.turns.map((t, i) => (
              <div key={i} className="turn block">
                <h3>
                  Turn {i + 1} · {formatTime(t.startedAt)}
                </h3>
                <pre className="pre">{t.prompt || '(preamble)'}</pre>
                {t.plan && (
                  <>
                    <h3 style={{ marginTop: 10 }}>Plan</h3>
                    <pre className="pre">{t.plan}</pre>
                  </>
                )}
                {t.actions.map((a, j) => (
                  <div key={j} className="action-row">
                    <span className="kind">{a.kind}</span>
                    <span>{a.summary}</span>
                  </div>
                ))}
              </div>
            ))}
            {detail.turns.length === 0 && <div className="empty">No turns</div>}
          </div>
        )}

        {tab === 'files' && (
          <div className="table-wrap">
            <table className="data">
              <thead>
                <tr>
                  <th>Path</th>
                  <th>Action</th>
                  <th>+/−</th>
                </tr>
              </thead>
              <tbody>
                {detail.files.map((f) => (
                  <tr key={f.path}>
                    <td className="mono">
                      <Link to={`/why?mode=file&path=${encodeURIComponent(f.path)}`}>
                        {basename(f.path)}
                      </Link>
                      <div className="dim">{f.path}</div>
                    </td>
                    <td>{f.action}</td>
                    <td className="mono">
                      {f.additions != null ? `+${f.additions}` : ''}{' '}
                      {f.deletions != null ? `−${f.deletions}` : ''}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            {detail.files.length === 0 && <div className="empty">No files</div>}
          </div>
        )}

        {tab === 'tests' && (
          <div className="stack">
            {detail.tests.map((t, i) => (
              <div key={i} className="block">
                <span className={`badge ${t.failed ? 'err' : 'ok'}`}>{t.status ?? t.phase}</span>{' '}
                {t.name ?? t.suite ?? 'test'}
                <div className="dim mono">
                  passed {t.passed ?? '—'} · failed {t.failed ?? '—'}
                </div>
              </div>
            ))}
            {detail.tests.length === 0 && <div className="empty">No tests</div>}
          </div>
        )}

        {tab === 'commands' && (
          <div className="stack">
            {detail.commands.map((c, i) => (
              <pre key={i} className="pre block">
                {c}
              </pre>
            ))}
            {detail.commands.length === 0 && <div className="empty">No commands</div>}
          </div>
        )}

        {tab === 'git' && (
          <div className="stack">
            <div className="block">
              <h3>HEAD</h3>
              <div className="mono">
                {shortSha(detail.gitHead)} → {shortSha(detail.gitHeadAfter)}
              </div>
            </div>
            <div className="block">
              <h3>Commits</h3>
              {detail.commits.map((c) => (
                <div key={c}>
                  <Link className="mono" to={`/why?mode=commit&sha=${encodeURIComponent(c)}`}>
                    {c}
                  </Link>
                </div>
              ))}
              {detail.commits.length === 0 && <div className="dim">—</div>}
            </div>
            <div className="block">
              <h3>Edges</h3>
              {detail.edges.map((e) => (
                <div key={`${e.from}-${e.to}-${e.kind}`} className="mono dim">
                  {e.from} -[{e.kind}
                  {e.label ? `:${e.label}` : ''}]→ {e.to}
                </div>
              ))}
            </div>
          </div>
        )}

        {tab === 'events' && (
          <pre className="pre block" style={{ maxHeight: 480, overflow: 'auto' }}>
            {events ? JSON.stringify(events, null, 2) : 'Loading…'}
          </pre>
        )}
      </div>
    </div>
  )
}
