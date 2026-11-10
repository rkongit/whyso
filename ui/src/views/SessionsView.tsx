import { useEffect, useMemo, useState } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import {
  ChevronLeft,
  CircleAlert,
  FileText,
  FlaskConical,
  GitCommitHorizontal,
  Inbox,
  MousePointerClick,
  Search,
  Terminal,
  Wrench,
  X,
  type LucideIcon,
} from 'lucide-react'
import { api, type Activity, type FileTouch, type SessionDetail, type SessionInfo } from '../api'
import { useAppData } from '../AppData'
import { errorMessage } from '../format'
import {
  basename,
  formatDateTime,
  formatDuration,
  formatRelativeTime,
  formatTime,
  shortSha,
  titleFromExcerpt,
} from '../format'
import { StatusBadge } from '../components/Badge'
import { CliHint, CopyButton } from '../components/CliHint'
import { EmptyState } from '../components/EmptyState'
import { SkeletonRows } from '../components/Skeleton'
import { Tabs, type TabItem } from '../components/Tabs'

type Tab = 'summary' | 'activity' | 'files' | 'tests' | 'commands' | 'git' | 'events'

function isFileTouchList(data: SessionInfo[] | FileTouch[]): data is FileTouch[] {
  return data.length > 0 && 'sessionId' in data[0]! && !('id' in data[0]!)
}

function activityIcon(kind: string): LucideIcon {
  const k = kind.toLowerCase()
  if (k.includes('error')) return CircleAlert
  if (k.includes('command')) return Terminal
  if (k.includes('file')) return FileText
  if (k.includes('test')) return FlaskConical
  if (k.includes('git') || k.includes('commit')) return GitCommitHorizontal
  return Wrench
}

function ErrorBanner({ message }: { message: string }) {
  return (
    <div className="banner" role="alert">
      <CircleAlert size={15} />
      <span>{message}</span>
    </div>
  )
}

export function SessionsView() {
  const { id } = useParams()
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()
  const { sessions: appSessions, refresh, loading } = useAppData()
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

  function applyFilters(nextFile = fileInput, nextPrefix = prefixInput) {
    const next = new URLSearchParams()
    if (nextFile.trim()) next.set('file', nextFile.trim())
    if (nextPrefix.trim()) next.set('prefix', nextPrefix.trim())
    if (id) {
      navigate(`/sessions/${encodeURIComponent(id)}${next.toString() ? `?${next}` : ''}`)
    } else {
      setParams(next)
    }
  }

  const scoped = Boolean(file || prefix)
  const qs = params.toString() ? `?${params}` : ''
  const open = (sessionId: string) => navigate(`/sessions/${encodeURIComponent(sessionId)}${qs}`)
  const cli = `whyso sessions${file ? ` --file ${file}` : ''}${prefix ? ` --prefix ${prefix}` : ''}`
  const count = fileTouches ? fileTouches.length : filtered.length

  return (
    <div className="page full">
      <div className={`split${id ? ' has-detail' : ''}`}>
        <div className="split-list">
          <div className="list-head">
            <div className="row">
              <h1 className="grow">Sessions</h1>
              <span className="badge">{count}</span>
            </div>
            <div className="input-icon">
              <Search size={15} />
              <input
                className="input"
                placeholder="Filter by prompt, id or label"
                aria-label="Filter sessions"
                value={filter}
                onChange={(e) => setFilter(e.target.value)}
              />
            </div>
            <form
              className="scope-form"
              onSubmit={(e) => {
                e.preventDefault()
                applyFilters()
              }}
            >
              <input
                className="input sm"
                placeholder="File path"
                aria-label="Only sessions that touched this file"
                value={fileInput}
                onChange={(e) => setFileInput(e.target.value)}
              />
              <input
                className="input sm"
                placeholder="Path prefix"
                aria-label="Only sessions that touched files under this prefix"
                value={prefixInput}
                onChange={(e) => setPrefixInput(e.target.value)}
              />
              <button type="submit" className="btn sm">
                Apply
              </button>
              {scoped && (
                <button
                  type="button"
                  className="btn sm ghost icon"
                  aria-label="Clear file filters"
                  title="Clear file filters"
                  onClick={() => applyFilters('', '')}
                >
                  <X size={14} />
                </button>
              )}
            </form>
            <CliHint command={cli} />
          </div>

          {listError && <ErrorBanner message={listError} />}

          <div className="list-scroll">
            {fileTouches
              ? fileTouches.map((t, i) => (
                  <button
                    key={`${t.sessionId}-${t.at}-${i}`}
                    type="button"
                    className={`list-item${id === t.sessionId ? ' active' : ''}`}
                    onClick={() => open(t.sessionId)}
                  >
                    <div className="title truncate">{titleFromExcerpt(t.promptExcerpt)}</div>
                    <div className="meta">
                      <span title={formatDateTime(t.at)}>{formatRelativeTime(t.at)}</span>
                      <span className="badge">{t.action}</span>
                      <span className="mono truncate">{basename(t.path)}</span>
                    </div>
                  </button>
                ))
              : filtered.map((n) => (
                  <button
                    key={n.id}
                    type="button"
                    className={`list-item${id === n.id ? ' active' : ''}`}
                    onClick={() => open(n.id)}
                  >
                    <div className="row">
                      <div className="title truncate grow">{titleFromExcerpt(n.promptExcerpt)}</div>
                      {n.status === 'active' && <StatusBadge status={n.status} />}
                    </div>
                    <div className="meta">
                      <span title={formatDateTime(n.startedAt)}>{formatRelativeTime(n.startedAt)}</span>
                      <span>{n.agent}</span>
                      <span className="meta-icon" title={`${n.fileCount} files changed`}>
                        <FileText size={12} />
                        {n.fileCount}
                      </span>
                      {n.commitCount > 0 && (
                        <span className="meta-icon" title={`${n.commitCount} commits`}>
                          <GitCommitHorizontal size={12} />
                          {n.commitCount}
                        </span>
                      )}
                    </div>
                    {(n.labels?.length ?? 0) > 0 && (
                      <div className="row wrap">
                        {n.labels.map((l) => (
                          <span key={l} className="badge accent">
                            {l}
                          </span>
                        ))}
                      </div>
                    )}
                  </button>
                ))}
            {count === 0 &&
              (loading && !scoped ? (
                <SkeletonRows rows={6} />
              ) : (
                <EmptyState
                  compact
                  icon={<Inbox size={20} />}
                  title={filter || scoped ? 'No matching sessions' : 'No sessions yet'}
                  hint={filter || scoped ? 'Try a different filter.' : undefined}
                  cli={filter || scoped ? undefined : 'whyso rebuild'}
                />
              ))}
          </div>
        </div>

        <div className="split-detail">
          {id ? (
            <SessionDetailView
              key={id}
              sessionId={id}
              backTo={`/sessions${qs}`}
              onChanged={() => void refresh()}
            />
          ) : (
            <EmptyState
              icon={<MousePointerClick size={20} />}
              title="Select a session"
              hint="Pick a session from the list to see its prompts, activity, files and commits."
            />
          )}
        </div>
      </div>
    </div>
  )
}

export function SessionDetailView({
  sessionId,
  backTo,
  onChanged,
}: {
  sessionId: string
  backTo?: string
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

  const back = backTo && (
    <Link className="back-link" to={backTo}>
      <ChevronLeft size={15} />
      All sessions
    </Link>
  )

  if (error) {
    return (
      <div className="detail-head">
        {back}
        <ErrorBanner message={error} />
      </div>
    )
  }
  if (!detail) {
    return (
      <>
        <div className="detail-head">{back}</div>
        <SkeletonRows rows={6} />
      </>
    )
  }

  async function changeLabel(action: 'add' | 'remove', value: string) {
    try {
      if (action === 'add') await api.addLabel(sessionId, value)
      else await api.removeLabel(sessionId, value)
      setDetail(await api.session(sessionId))
      await refresh()
      onChanged?.()
      setToast(action === 'add' ? `Labeled as ${value}` : `Removed label ${value}`)
      if (action === 'add') setLabel('')
    } catch (e) {
      setToast(errorMessage(e), 'error')
    }
  }

  const tabs: TabItem<Tab>[] = [
    { id: 'summary', label: 'Summary' },
    { id: 'activity', label: 'Activity', count: detail.turns.length },
    { id: 'files', label: 'Files', count: detail.files.length },
    { id: 'tests', label: 'Tests', count: detail.tests.length },
    { id: 'commands', label: 'Commands', count: detail.commands.length },
    { id: 'git', label: 'Git', count: detail.commits.length },
    { id: 'events', label: 'Events' },
  ]
  const eventsJson = events ? JSON.stringify(events, null, 2) : ''

  return (
    <>
      <div className="detail-sticky">
        <div className="detail-head">
          {back}
          <h1>{titleFromExcerpt(detail.prompts[0])}</h1>
          <div className="row wrap">
            <StatusBadge status={detail.status} />
            <span className="badge">{detail.agent}</span>
            {detail.durationSeconds != null && (
              <span className="badge" title="Duration">
                {formatDuration(detail.durationSeconds)}
              </span>
            )}
            <span className="dim">{formatDateTime(detail.startedAt)}</span>
          </div>
          <div className="row wrap">
            {detail.labels.map((l) => (
              <span key={l} className="label-chip">
                {l}
                <button
                  type="button"
                  aria-label={`Remove label ${l}`}
                  onClick={() => void changeLabel('remove', l)}
                >
                  <X size={11} />
                </button>
              </span>
            ))}
            <input
              className="input sm label-input"
              placeholder="Add label…"
              aria-label="Add label"
              value={label}
              onChange={(e) => setLabel(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && label.trim()) void changeLabel('add', label.trim())
              }}
            />
          </div>
          <div className="row wrap">
            <span className="mono dim">{detail.id}</span>
            <CopyButton text={detail.id} label="Copy session id" />
          </div>
        </div>
        <Tabs tabs={tabs} value={tab} onChange={setTab} />
      </div>

      <div className="detail-body" role="tabpanel">
        {tab === 'summary' && (
          <div className="stack">
            <CliHint command={`whyso session ${sessionId}`} />
            <div className="block">
              <h3 className="section-title">First prompt</h3>
              <pre className="pre clamp">{detail.prompts[0] || '—'}</pre>
            </div>
            {detail.responses.length > 0 && (
              <div className="block">
                <h3 className="section-title">Last response</h3>
                <pre className="pre clamp">{detail.responses[detail.responses.length - 1]}</pre>
              </div>
            )}
            <div className="block">
              <h3 className="section-title">Afterwards</h3>
              {detail.afterwards.length === 0 && <div className="dim">No follow-up sessions</div>}
              <div className="stack tight">
                {detail.afterwards.map((a) => (
                  <div key={a.id}>
                    <Link to={`/sessions/${encodeURIComponent(a.id)}`}>
                      {titleFromExcerpt(a.promptExcerpt)}
                    </Link>
                    <div className="dim">
                      {a.via ? `via ${a.via} · ` : ''}
                      {formatDateTime(a.startedAt)}
                    </div>
                  </div>
                ))}
              </div>
            </div>
          </div>
        )}

        {tab === 'activity' && (
          <div className="stack">
            <CliHint command={`whyso activity ${sessionId}`} />
            {detail.turns.length === 0 ? (
              <EmptyState compact title="No turns recorded" />
            ) : (
              <ol className="timeline">
                {detail.turns.map((t, i) => (
                  <li key={i} className="turn">
                    <div className="turn-marker">{i + 1}</div>
                    <div className="turn-body">
                      <div className="turn-meta">
                        <span>{t.prompt ? 'Prompt' : 'Before first prompt'}</span>
                        <span className="dim">{formatTime(t.startedAt)}</span>
                        {t.commits.map((c) => (
                          <Link
                            key={c}
                            className="badge info mono"
                            to={`/why?mode=commit&sha=${encodeURIComponent(c)}`}
                          >
                            {shortSha(c)}
                          </Link>
                        ))}
                      </div>
                      {t.prompt && <pre className="pre clamp prompt-text">{t.prompt}</pre>}
                      {t.actions.length > 0 && (
                        <ul className="actions">
                          {t.actions.map((a, j) => (
                            <ActionRow key={j} action={a} />
                          ))}
                        </ul>
                      )}
                      {t.plan && (
                        <details className="response">
                          <summary>Agent response</summary>
                          <pre className="pre clamp">{t.plan}</pre>
                        </details>
                      )}
                    </div>
                  </li>
                ))}
              </ol>
            )}
          </div>
        )}

        {tab === 'files' &&
          (detail.files.length === 0 ? (
            <EmptyState compact title="No files changed" />
          ) : (
            <div className="panel table-wrap">
              <table className="data">
                <thead>
                  <tr>
                    <th>Path</th>
                    <th>Action</th>
                    <th>Lines</th>
                  </tr>
                </thead>
                <tbody>
                  {detail.files.map((f) => (
                    <tr key={f.path}>
                      <td>
                        <Link
                          className="mono"
                          to={`/why?mode=file&path=${encodeURIComponent(f.path)}`}
                          title="Trace this file"
                        >
                          {basename(f.path)}
                        </Link>
                        <div className="mono dim">{f.path}</div>
                      </td>
                      <td>
                        <span className="badge">{f.action}</span>
                      </td>
                      <td className="mono">
                        {f.additions != null && <span className="num-add">+{f.additions}</span>}{' '}
                        {f.deletions != null && <span className="num-del">−{f.deletions}</span>}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ))}

        {tab === 'tests' && (
          <div className="stack tight">
            {detail.tests.map((t, i) => (
              <div key={i} className="block row">
                <FlaskConical size={15} className="dim" />
                <div className="grow">
                  <div>{t.name ?? t.suite ?? 'test'}</div>
                  <div className="dim mono">
                    passed {t.passed ?? '—'} · failed {t.failed ?? '—'}
                  </div>
                </div>
                <span className={`badge ${t.failed ? 'err' : 'ok'}`}>{t.status ?? t.phase}</span>
              </div>
            ))}
            {detail.tests.length === 0 && <EmptyState compact title="No test runs" />}
          </div>
        )}

        {tab === 'commands' && (
          <div className="stack tight">
            {detail.commands.map((c, i) => (
              <div key={i} className="code-block">
                <pre>{c}</pre>
                <CopyButton text={c} label="Copy command" />
              </div>
            ))}
            {detail.commands.length === 0 && <EmptyState compact title="No commands run" />}
          </div>
        )}

        {tab === 'git' && (
          <div className="stack">
            <div className="block">
              <h3 className="section-title">HEAD</h3>
              <div className="mono">
                {shortSha(detail.gitHead)} → {shortSha(detail.gitHeadAfter)}
              </div>
            </div>
            <div className="block">
              <h3 className="section-title">Commits</h3>
              <div className="stack tight">
                {detail.commits.map((c) => (
                  <Link key={c} className="mono" to={`/why?mode=commit&sha=${encodeURIComponent(c)}`}>
                    {c}
                  </Link>
                ))}
              </div>
              {detail.commits.length === 0 && <div className="dim">No commits linked</div>}
            </div>
            <div className="block">
              <h3 className="section-title">Edges</h3>
              <div className="stack tight">
                {detail.edges.map((e) => {
                  const outgoing = e.from === detail.id
                  const other = outgoing ? e.to : e.from
                  return (
                    <div key={`${e.from}-${e.to}-${e.kind}`} className="row wrap">
                      <span className="dim">{outgoing ? '→' : '←'}</span>
                      <Link className="mono" to={`/sessions/${encodeURIComponent(other)}`}>
                        {other}
                      </Link>
                      <span className={`badge${e.manual ? ' accent' : ''}`}>{e.kind}</span>
                      {e.label && <span className="badge">{e.label}</span>}
                    </div>
                  )
                })}
              </div>
              {detail.edges.length === 0 && <div className="dim">No edges</div>}
            </div>
          </div>
        )}

        {tab === 'events' && (
          <div className="stack">
            <CliHint command={`whyso events ${sessionId}`} />
            {events ? (
              <div className="code-block">
                <pre>{eventsJson}</pre>
                <CopyButton text={eventsJson} label="Copy JSON" />
              </div>
            ) : (
              <SkeletonRows rows={8} />
            )}
          </div>
        )}
      </div>
    </>
  )
}

function ActionRow({ action }: { action: Activity }) {
  const Icon = activityIcon(action.kind)
  const failed = action.kind.toLowerCase().includes('error') || Boolean(action.errorMessage)
  return (
    <li className={`action${failed ? ' failed' : ''}`}>
      <Icon size={14} />
      <span className="kind">{action.kind}</span>
      <span className="summary">{action.summary}</span>
    </li>
  )
}
