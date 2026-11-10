import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { api, type ProvenanceResult } from '../api'
import { formatTime, titleFromExcerpt } from '../format'

export function WhyView() {
  const [params, setParams] = useSearchParams()
  const mode = (params.get('mode') as 'commit' | 'file' | null) ?? 'commit'
  const [sha, setSha] = useState(params.get('sha') ?? '')
  const [path, setPath] = useState(params.get('path') ?? '')
  const [lines, setLines] = useState(params.get('lines') ?? '')
  const [result, setResult] = useState<ProvenanceResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    setSha(params.get('sha') ?? '')
    setPath(params.get('path') ?? '')
    setLines(params.get('lines') ?? '')
  }, [params])

  useEffect(() => {
    const m = params.get('mode') ?? 'commit'
    const s = params.get('sha')
    const p = params.get('path')
    const l = params.get('lines') ?? undefined
    if (m === 'commit' && !s) {
      setResult(null)
      return
    }
    if (m === 'file' && !p) {
      setResult(null)
      return
    }
    let cancelled = false
    setLoading(true)
    const req =
      m === 'file' && p
        ? api.whyFile(p, l || undefined)
        : s
          ? api.whyCommit(s)
          : Promise.resolve(null)
    req
      .then((r) => {
        if (!cancelled) {
          setResult(r)
          setError(null)
        }
      })
      .catch((e: Error) => {
        if (!cancelled) setError(e.message)
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [params])

  function run() {
    const next = new URLSearchParams()
    next.set('mode', mode)
    if (mode === 'commit') next.set('sha', sha.trim())
    else {
      next.set('path', path.trim())
      if (lines.trim()) next.set('lines', lines.trim())
    }
    setParams(next)
  }

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h1>Why</h1>
          <p>Trace a commit or file back to prompts — <code>whyso why</code>.</p>
        </div>
      </div>

      <div className="toolbar">
        <div className="field">
          <label>Mode</label>
          <select
            value={mode}
            onChange={(e) => {
              const next = new URLSearchParams(params)
              next.set('mode', e.target.value)
              setParams(next)
            }}
          >
            <option value="commit">commit</option>
            <option value="file">file</option>
          </select>
        </div>
        {mode === 'commit' ? (
          <div className="field" style={{ flex: 1 }}>
            <label htmlFor="why-sha">SHA</label>
            <input
              id="why-sha"
              value={sha}
              onChange={(e) => setSha(e.target.value)}
              placeholder="full or prefix"
              onKeyDown={(e) => e.key === 'Enter' && run()}
            />
          </div>
        ) : (
          <>
            <div className="field" style={{ flex: 1 }}>
              <label htmlFor="why-path">Path</label>
              <input
                id="why-path"
                value={path}
                onChange={(e) => setPath(e.target.value)}
                placeholder="src/…"
                onKeyDown={(e) => e.key === 'Enter' && run()}
              />
            </div>
            <div className="field">
              <label htmlFor="why-lines">--lines</label>
              <input
                id="why-lines"
                value={lines}
                onChange={(e) => setLines(e.target.value)}
                placeholder="10-42"
              />
            </div>
          </>
        )}
        <button type="button" className="btn primary" onClick={run} style={{ alignSelf: 'end' }}>
          Trace
        </button>
      </div>

      <div className="cli-hint" style={{ marginBottom: 12 }}>
        {mode === 'commit'
          ? `whyso why commit ${sha || '<sha>'}`
          : `whyso why file ${path || '<path>'}${lines ? ` --lines ${lines}` : ''}`}
      </div>

      {error && <div className="error-banner">{error}</div>}
      {loading && <div className="muted">Tracing…</div>}

      {result && (
        <div className="panel">
          <div className="panel-head">
            <span>
              {result.subjectKind}
              {result.sha ? ` · ${result.sha}` : ''}
              {result.path ? ` · ${result.path}` : ''}
              {result.startLine != null ? `:${result.startLine}-${result.endLine}` : ''}
            </span>
            <span>{result.hits.length} hit(s)</span>
          </div>
          {result.hits.length === 0 ? (
            <div className="empty">No captured provenance</div>
          ) : (
            <div className="stack" style={{ padding: 12 }}>
              {result.hits.map((h, i) => (
                <div key={i} className="block">
                  <div style={{ marginBottom: 6 }}>
                    <Link to={`/sessions/${encodeURIComponent(h.workNodeId)}`}>{h.workNodeId}</Link>{' '}
                    <span className="badge accent">{h.matchKind}</span>
                    <span className="dim"> · {formatTime(h.startedAt)}</span>
                  </div>
                  <pre className="pre">{titleFromExcerpt(h.prompt, h.prompt || '—')}</pre>
                  {h.plan && (
                    <pre className="pre dim" style={{ marginTop: 8 }}>
                      {h.plan.slice(0, 600)}
                      {h.plan.length > 600 ? '…' : ''}
                    </pre>
                  )}
                  {h.files.length > 0 && (
                    <div className="mono dim" style={{ marginTop: 6 }}>
                      files: {h.files.slice(0, 5).join(', ')}
                    </div>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  )
}
