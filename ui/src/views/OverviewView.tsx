import { Link } from 'react-router-dom'
import { Activity, FileText, History, Inbox, MessageSquareText, RefreshCw, Wrench } from 'lucide-react'
import { api } from '../api'
import { useAppData } from '../AppData'
import { errorMessage } from '../format'
import { useConfirm } from '../confirm'
import { formatDateTime, formatRelativeTime, titleFromExcerpt } from '../format'
import { StatusBadge } from '../components/Badge'
import { CliHint } from '../components/CliHint'
import { EmptyState } from '../components/EmptyState'
import { SkeletonRows } from '../components/Skeleton'

export function OverviewView() {
  const { status, sessions, refresh, setToast, loading } = useAppData()
  const confirm = useConfirm()
  const recent = sessions.slice(0, 8)
  const firstLoad = loading && !status

  async function rebuild() {
    const ok = await confirm({
      title: 'Rebuild projections',
      message: 'Rebuild all session projections and auto edges?',
      confirmLabel: 'Rebuild',
    })
    if (!ok) return
    try {
      const r = await api.rebuild()
      await refresh()
      setToast(`Rebuilt ${r.nodeCount} sessions`)
    } catch (e) {
      setToast(errorMessage(e), 'error')
    }
  }

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h1>Overview</h1>
          <p>Project status and recent sessions.</p>
        </div>
        <div className="page-actions">
          <button type="button" className="btn" onClick={() => void refresh()} disabled={loading}>
            <RefreshCw size={15} className={loading ? 'spin' : undefined} />
            Refresh
          </button>
          <button type="button" className="btn primary" onClick={() => void rebuild()}>
            <Wrench size={15} />
            Rebuild
          </button>
        </div>
      </div>

      <div className="stats">
        <div className="stat">
          <div className="stat-icon">
            <Activity size={17} />
          </div>
          <div>
            <div className="label">Daemon</div>
            <div className={`value${status ? (status.daemonRunning ? ' up' : ' down') : ''}`}>
              {status ? (status.daemonRunning ? 'Running' : 'Stopped') : '—'}
            </div>
            <div className="hint">{status?.daemonStatus ?? ' '}</div>
          </div>
        </div>
        <div className="stat">
          <div className="stat-icon">
            <History size={17} />
          </div>
          <div>
            <div className="label">Sessions</div>
            <div className="value">{status?.sessionCount ?? '—'}</div>
            <div className="hint">captured agent conversations</div>
          </div>
        </div>
        <div className="stat">
          <div className="stat-icon">
            <MessageSquareText size={17} />
          </div>
          <div>
            <div className="label">Prompts</div>
            <div className="value">{status?.promptCount ?? '—'}</div>
            <div className="hint">across all sessions</div>
          </div>
        </div>
      </div>

      <div className="panel">
        <div className="panel-head">
          <span>Project</span>
          <CliHint command="whyso status" />
        </div>
        <dl className="kv">
          <dt>Root</dt>
          <dd className="mono">{status?.root ?? '—'}</dd>
          <dt>Project id</dt>
          <dd className="mono">{status?.projectId ?? '—'}</dd>
          <dt>Daemon address</dt>
          <dd className="mono">{status?.bind ?? '—'}</dd>
        </dl>
      </div>

      <div className="panel">
        <div className="panel-head">
          <span>Recent sessions</span>
          <Link to="/sessions">View all</Link>
        </div>
        {firstLoad ? (
          <SkeletonRows rows={5} />
        ) : recent.length === 0 ? (
          <EmptyState
            icon={<Inbox size={20} />}
            title="No sessions yet"
            hint="Start an agent session in this project, or rebuild projections from the event log."
            cli="whyso rebuild"
          />
        ) : (
          <div className="session-rows">
            {recent.map((n) => (
              <Link key={n.id} className="session-row" to={`/sessions/${encodeURIComponent(n.id)}`}>
                <div className="grow">
                  <div className="title truncate">{titleFromExcerpt(n.promptExcerpt)}</div>
                  <div className="meta">
                    <span title={formatDateTime(n.startedAt)}>{formatRelativeTime(n.startedAt)}</span>
                    <span>{n.agent}</span>
                    <span className="meta-icon">
                      <FileText size={12} />
                      {n.fileCount}
                    </span>
                    {n.labels.map((l) => (
                      <span key={l} className="badge accent">
                        {l}
                      </span>
                    ))}
                  </div>
                </div>
                <StatusBadge status={n.status} />
              </Link>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}
