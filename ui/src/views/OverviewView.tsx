import { Link } from 'react-router-dom'
import { api } from '../api'
import { useAppData } from '../AppData'
import { formatDay, shortSha, titleFromExcerpt } from '../format'
import worktreeIcon from '../assets/worktree-icon.png'

export function OverviewView() {
  const { status, sessions, refresh, setToast, loading } = useAppData()
  const recent = sessions.slice(0, 8)

  return (
    <div className="page">
      <div className="page-header">
        <div className="page-brand">
          <img className="page-icon" src={worktreeIcon} alt="Worktree" width={48} height={48} />
          <div>
            <h1>Overview</h1>
            <p>Project status and recent sessions — equivalent to <code>worktree status</code>.</p>
          </div>
        </div>
        <div className="page-actions">
          <button type="button" className="btn" onClick={() => void refresh()} disabled={loading}>
            Refresh
          </button>
          <button
            type="button"
            className="btn primary"
            onClick={() => {
              if (!window.confirm('Rebuild all session projections and auto edges?')) return
              void (async () => {
                try {
                  const r = await api.rebuild()
                  await refresh()
                  setToast(`Rebuilt ${r.nodeCount} sessions`)
                } catch (e) {
                  setToast(e instanceof Error ? e.message : String(e))
                }
              })()
            }}
          >
            Rebuild
          </button>
        </div>
      </div>

      <div className="stats">
        <div className="stat">
          <div className="label">Daemon</div>
          <div className="value" style={{ fontSize: 16 }}>
            {status?.daemonRunning ? 'up' : 'down'}
          </div>
          <div className="hint">{status?.daemonStatus ?? '—'}</div>
        </div>
        <div className="stat">
          <div className="label">Sessions</div>
          <div className="value">{status?.sessionCount ?? '—'}</div>
        </div>
        <div className="stat">
          <div className="label">Prompts</div>
          <div className="value">{status?.promptCount ?? '—'}</div>
        </div>
      </div>

      <div className="panel">
        <div className="panel-head">
          <span>Project</span>
          <span className="mono dim">{status?.bind ?? ''}</span>
        </div>
        <div style={{ padding: 12 }}>
          <div className="mono">{status?.root ?? '—'}</div>
          <div className="dim" style={{ marginTop: 4 }}>
            project id · {status?.projectId ?? '—'}
          </div>
          <div className="cli-hint">worktree status</div>
        </div>
      </div>

      <div className="panel">
        <div className="panel-head">
          <span>Recent sessions</span>
          <Link to="/sessions">View all</Link>
        </div>
        {recent.length === 0 ? (
          <div className="empty">
            No sessions yet.
            <br />
            <code>worktree rebuild</code>
          </div>
        ) : (
          <div className="table-wrap">
            <table className="data">
              <thead>
                <tr>
                  <th>When</th>
                  <th>Prompt</th>
                  <th>Status</th>
                  <th>Files</th>
                  <th>Id</th>
                </tr>
              </thead>
              <tbody>
                {recent.map((n) => (
                  <tr key={n.id}>
                    <td className="mono dim">{formatDay(n.startedAt)}</td>
                    <td>
                      <Link to={`/sessions/${encodeURIComponent(n.id)}`}>
                        {titleFromExcerpt(n.promptExcerpt)}
                      </Link>
                    </td>
                    <td>
                      <span className={`badge ${n.status === 'active' ? 'warn' : 'ok'}`}>
                        {n.status}
                      </span>
                    </td>
                    <td className="mono">{n.fileCount}</td>
                    <td className="mono dim">{shortSha(n.id, 12)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  )
}
