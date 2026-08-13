import { useState } from 'react'
import { Link } from 'react-router-dom'
import { api, type Edge, type NodeDetail, type SessionInfo } from '../api'
import { formatTime, shortSha, titleFromExcerpt } from '../format'
import { useAppData } from '../AppData'

interface Props {
  nodeId: string | null
  detail: NodeDetail | null
  edges?: Edge[]
  onClose?: () => void
  onChanged?: () => void
}

export function Inspector({ nodeId, detail, edges, onClose, onChanged }: Props) {
  const { sessions, refresh, setToast } = useAppData()
  const [label, setLabel] = useState('')
  const [linkTo, setLinkTo] = useState('')
  const [linkLabel, setLinkLabel] = useState('')
  const [busy, setBusy] = useState(false)
  const listItem: SessionInfo | undefined = sessions.find((n) => n.id === nodeId)

  if (!nodeId) {
    return (
      <aside className="inspector">
        <p className="muted">Select a node to inspect.</p>
      </aside>
    )
  }

  const labels = detail?.labels ?? listItem?.labels ?? []
  const related = (edges ?? detail?.edges ?? []).filter(
    (e) => e.from === nodeId || e.to === nodeId,
  )

  async function withBusy(fn: () => Promise<void>) {
    setBusy(true)
    try {
      await fn()
      await refresh()
      onChanged?.()
    } catch (e) {
      setToast(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <aside className="inspector">
      <div style={{ display: 'flex', justifyContent: 'space-between', gap: 8 }}>
        <h2>{titleFromExcerpt(detail?.prompts[0] ?? listItem?.promptExcerpt)}</h2>
        {onClose && (
          <button type="button" className="btn" onClick={onClose}>
            ×
          </button>
        )}
      </div>
      <div className="mono dim">{nodeId}</div>
      <div style={{ marginTop: 8 }}>
        <span className={`badge ${detail?.status === 'active' || listItem?.status === 'active' ? 'warn' : 'ok'}`}>
          {detail?.status ?? listItem?.status ?? '—'}
        </span>
        <span className="badge">{detail?.agent ?? listItem?.agent}</span>
      </div>

      <div className="section">
        <h3>Labels</h3>
        <div className="label-row">
          {labels.map((l) => (
            <span key={l} className="label-chip">
              {l}
              <button
                type="button"
                disabled={busy}
                title="Remove label"
                onClick={() =>
                  void withBusy(async () => {
                    await api.removeLabel(nodeId, l)
                    setToast(`Removed label ${l}`)
                  })
                }
              >
                ×
              </button>
            </span>
          ))}
          {labels.length === 0 && <span className="dim">none</span>}
        </div>
        <div className="prompt-row" style={{ paddingLeft: 0 }}>
          <input
            className="inline-input"
            placeholder="feature | fix | …"
            value={label}
            onChange={(e) => setLabel(e.target.value)}
          />
          <button
            type="button"
            className="btn primary"
            disabled={busy || !label.trim()}
            onClick={() =>
              void withBusy(async () => {
                await api.addLabel(nodeId, label.trim())
                setToast(`Labeled as ${label.trim()}`)
                setLabel('')
              })
            }
          >
            Add
          </button>
        </div>
        <div className="cli-hint">worktree label {nodeId} …</div>
      </div>

      <div className="section">
        <h3>Link</h3>
        <div className="stack">
          <input
            className="inline-input"
            placeholder="to sess_…"
            value={linkTo}
            onChange={(e) => setLinkTo(e.target.value)}
          />
          <input
            className="inline-input"
            placeholder="label (optional)"
            value={linkLabel}
            onChange={(e) => setLinkLabel(e.target.value)}
          />
          <button
            type="button"
            className="btn primary"
            disabled={busy || !linkTo.trim()}
            onClick={() =>
              void withBusy(async () => {
                await api.link(nodeId, linkTo.trim(), linkLabel.trim() || undefined)
                setToast(`Linked → ${linkTo.trim()}`)
                setLinkTo('')
                setLinkLabel('')
              })
            }
          >
            Link
          </button>
        </div>
        <div className="cli-hint">worktree link {nodeId} &lt;to&gt;</div>
      </div>

      <div className="section">
        <h3>Edges</h3>
        {related.length === 0 && <div className="dim">No edges</div>}
        <div className="stack">
          {related.map((e) => (
            <div key={`${e.from}-${e.to}-${e.kind}`} className="block">
              <div className="mono" style={{ fontSize: 11 }}>
                {e.from === nodeId ? '→' : '←'} {e.from === nodeId ? e.to : e.from}
              </div>
              <div className="muted">
                {e.kind}
                {e.label ? ` · ${e.label}` : ''}
                {e.manual ? ' · manual' : ''}
              </div>
              {e.manual && (
                <button
                  type="button"
                  className="btn danger"
                  style={{ marginTop: 6 }}
                  disabled={busy}
                  onClick={() =>
                    void withBusy(async () => {
                      await api.unlink(e.from, e.to, e.kind)
                      setToast('Edge removed')
                    })
                  }
                >
                  Unlink
                </button>
              )}
            </div>
          ))}
        </div>
      </div>

      {detail && (
        <div className="section">
          <h3>Snapshot</h3>
          <div className="muted">
            {detail.turns.length} turns · {detail.files.length} files · {detail.commits.length}{' '}
            commits
          </div>
          {detail.gitHead && (
            <div className="mono dim" style={{ marginTop: 4 }}>
              {shortSha(detail.gitHead)} → {shortSha(detail.gitHeadAfter)}
            </div>
          )}
          <div className="dim" style={{ marginTop: 4 }}>
            {formatTime(detail.startedAt)}
          </div>
        </div>
      )}

      <div className="section">
        <Link className="btn" to={`/sessions/${encodeURIComponent(nodeId)}`}>
          Open session detail
        </Link>
      </div>
    </aside>
  )
}
