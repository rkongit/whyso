import { useState } from 'react'
import { Link } from 'react-router-dom'
import { ArrowLeft, ArrowRight, ExternalLink, MousePointerClick, Plus, X } from 'lucide-react'
import { api, type Edge, type NodeDetail, type SessionInfo } from '../api'
import { formatDateTime, shortSha, titleFromExcerpt } from '../format'
import { useAppData } from '../AppData'
import { errorMessage } from '../format'
import { useConfirm } from '../confirm'
import { StatusBadge } from './Badge'
import { CliHint } from './CliHint'
import { EmptyState } from './EmptyState'

interface Props {
  nodeId: string | null
  detail: NodeDetail | null
  edges?: Edge[]
  onClose?: () => void
  onChanged?: () => void
}

export function Inspector({ nodeId, detail, edges, onClose, onChanged }: Props) {
  const { sessions, refresh, setToast } = useAppData()
  const confirm = useConfirm()
  const [label, setLabel] = useState('')
  const [linkTo, setLinkTo] = useState('')
  const [linkLabel, setLinkLabel] = useState('')
  const [busy, setBusy] = useState(false)
  const listItem: SessionInfo | undefined = sessions.find((n) => n.id === nodeId)

  if (!nodeId) {
    return (
      <aside className="inspector is-empty">
        <EmptyState
          icon={<MousePointerClick size={20} />}
          title="No session selected"
          hint="Select a node in the tree to see its labels and links."
        />
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
      setToast(errorMessage(e), 'error')
    } finally {
      setBusy(false)
    }
  }

  function addLabel() {
    const value = label.trim()
    if (!value) return
    void withBusy(async () => {
      await api.addLabel(nodeId!, value)
      setToast(`Labeled as ${value}`)
      setLabel('')
    })
  }

  return (
    <aside className="inspector">
      <div className="inspector-head">
        <div className="grow">
          <h2>{titleFromExcerpt(detail?.prompts[0] ?? listItem?.promptExcerpt)}</h2>
          <div className="mono dim truncate">{nodeId}</div>
        </div>
        {onClose && (
          <button type="button" className="btn ghost icon sm" onClick={onClose} aria-label="Close inspector">
            <X size={15} />
          </button>
        )}
      </div>
      <div className="row wrap">
        <StatusBadge status={detail?.status ?? listItem?.status} />
        <span className="badge">{detail?.agent ?? listItem?.agent ?? '—'}</span>
      </div>
      <Link className="btn block" to={`/sessions/${encodeURIComponent(nodeId)}`}>
        <ExternalLink size={14} />
        Open session
      </Link>

      <section className="section">
        <h3 className="section-title">Labels</h3>
        <div className="row wrap">
          {labels.map((l) => (
            <span key={l} className="label-chip">
              {l}
              <button
                type="button"
                disabled={busy}
                aria-label={`Remove label ${l}`}
                onClick={() =>
                  void withBusy(async () => {
                    await api.removeLabel(nodeId, l)
                    setToast(`Removed label ${l}`)
                  })
                }
              >
                <X size={11} />
              </button>
            </span>
          ))}
          {labels.length === 0 && <span className="dim">No labels</span>}
        </div>
        <div className="row">
          <input
            className="input sm"
            placeholder="feature, fix, refactor…"
            aria-label="New label"
            value={label}
            onChange={(e) => setLabel(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && addLabel()}
          />
          <button
            type="button"
            className="btn sm primary"
            disabled={busy || !label.trim()}
            onClick={addLabel}
          >
            <Plus size={14} />
            Add
          </button>
        </div>
        <CliHint command={`whyso label ${nodeId} <label>`} />
      </section>

      <section className="section">
        <h3 className="section-title">Link to another session</h3>
        <input
          className="input sm"
          placeholder="Target session id (sess_…)"
          aria-label="Target session id"
          value={linkTo}
          onChange={(e) => setLinkTo(e.target.value)}
        />
        <div className="row">
          <input
            className="input sm"
            placeholder="Label (optional)"
            aria-label="Link label"
            value={linkLabel}
            onChange={(e) => setLinkLabel(e.target.value)}
          />
          <button
            type="button"
            className="btn sm primary"
            disabled={busy || !linkTo.trim()}
            onClick={() =>
              void withBusy(async () => {
                await api.link(nodeId, linkTo.trim(), linkLabel.trim() || undefined)
                setToast(`Linked to ${linkTo.trim()}`)
                setLinkTo('')
                setLinkLabel('')
              })
            }
          >
            Link
          </button>
        </div>
        <CliHint command={`whyso link ${nodeId} <to>`} />
      </section>

      <section className="section">
        <h3 className="section-title">Edges</h3>
        {related.length === 0 && <div className="dim">No edges</div>}
        {related.map((e) => {
          const outgoing = e.from === nodeId
          const other = outgoing ? e.to : e.from
          return (
            <div key={`${e.from}-${e.to}-${e.kind}`} className="edge-row">
              {outgoing ? <ArrowRight size={14} /> : <ArrowLeft size={14} />}
              <div className="grow">
                <div className="mono truncate" title={other}>
                  {other}
                </div>
                <div className="row wrap">
                  <span className={`badge${e.manual ? ' accent' : ''}`}>{e.kind}</span>
                  {e.label && <span className="badge">{e.label}</span>}
                </div>
              </div>
              {e.manual && (
                <button
                  type="button"
                  className="btn sm danger"
                  disabled={busy}
                  onClick={() =>
                    void (async () => {
                      const ok = await confirm({
                        title: 'Remove link',
                        message: `Unlink ${e.from} → ${e.to}?`,
                        confirmLabel: 'Unlink',
                        danger: true,
                      })
                      if (!ok) return
                      await withBusy(async () => {
                        await api.unlink(e.from, e.to, e.kind)
                        setToast('Link removed')
                      })
                    })()
                  }
                >
                  Unlink
                </button>
              )}
            </div>
          )
        })}
      </section>

      {detail && (
        <section className="section">
          <h3 className="section-title">Snapshot</h3>
          <div className="muted">
            {detail.turns.length} turns · {detail.files.length} files · {detail.commits.length} commits
          </div>
          {detail.gitHead && (
            <div className="mono dim">
              {shortSha(detail.gitHead)} → {shortSha(detail.gitHeadAfter)}
            </div>
          )}
          <div className="dim">{formatDateTime(detail.startedAt)}</div>
        </section>
      )}
    </aside>
  )
}
