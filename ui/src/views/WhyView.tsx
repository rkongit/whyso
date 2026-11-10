import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { CircleAlert, FileText, GitCommitHorizontal, SearchX, Waypoints } from 'lucide-react'
import { api, type ProvenanceResult } from '../api'
import { formatDateTime, shortSha, titleFromExcerpt } from '../format'
import { CliHint } from '../components/CliHint'
import { EmptyState } from '../components/EmptyState'
import { SkeletonRows } from '../components/Skeleton'

type Mode = 'commit' | 'file'

export function WhyView() {
  const [params, setParams] = useSearchParams()
  const mode: Mode = params.get('mode') === 'file' ? 'file' : 'commit'
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
    setError(null)
    if ((m === 'commit' && !s) || (m === 'file' && !p)) {
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
        if (!cancelled) setResult(r)
      })
      .catch((e: Error) => {
        if (!cancelled) {
          setResult(null)
          setError(e.message)
        }
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

  function setMode(m: Mode) {
    const next = new URLSearchParams(params)
    next.set('mode', m)
    setParams(next)
  }

  const cli =
    mode === 'commit'
      ? `whyso why commit ${sha || '<sha>'}`
      : `whyso why file ${path || '<path>'}${lines ? ` --lines ${lines}` : ''}`

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h1>Why</h1>
          <p>Trace a commit or a file back to the prompts that produced it.</p>
        </div>
        <CliHint command={cli} />
      </div>

      <form
        className="panel panel-body toolbar"
        onSubmit={(e) => {
          e.preventDefault()
          run()
        }}
      >
        <div className="field fit">
          <span className="field-label">Trace a</span>
          <div className="segmented" role="radiogroup" aria-label="What to trace">
            <button type="button" role="radio" aria-checked={mode === 'commit'} onClick={() => setMode('commit')}>
              <GitCommitHorizontal size={15} />
              Commit
            </button>
            <button type="button" role="radio" aria-checked={mode === 'file'} onClick={() => setMode('file')}>
              <FileText size={15} />
              File
            </button>
          </div>
        </div>
        {mode === 'commit' ? (
          <div className="field grow">
            <label htmlFor="why-sha">Commit SHA</label>
            <input
              id="why-sha"
              className="mono"
              value={sha}
              onChange={(e) => setSha(e.target.value)}
              placeholder="Full SHA or prefix"
            />
          </div>
        ) : (
          <>
            <div className="field grow">
              <label htmlFor="why-path">File path</label>
              <input
                id="why-path"
                className="mono"
                value={path}
                onChange={(e) => setPath(e.target.value)}
                placeholder="src/…"
              />
            </div>
            <div className="field narrow">
              <label htmlFor="why-lines">Lines (optional)</label>
              <input
                id="why-lines"
                className="mono"
                value={lines}
                onChange={(e) => setLines(e.target.value)}
                placeholder="10-42"
              />
            </div>
          </>
        )}
        <button type="submit" className="btn primary">
          <Waypoints size={15} />
          Trace
        </button>
      </form>

      {error && (
        <div className="banner" role="alert">
          <CircleAlert size={15} />
          <span>{error}</span>
        </div>
      )}

      {loading ? (
        <div className="panel">
          <SkeletonRows rows={4} />
        </div>
      ) : result ? (
        <>
          <div className="result-head">
            <span className="badge">{result.subjectKind}</span>
            <span className="mono truncate">
              {result.sha ?? result.path}
              {result.startLine != null ? `:${result.startLine}-${result.endLine}` : ''}
            </span>
            <span className="grow" />
            <span className="dim">
              {result.hits.length} {result.hits.length === 1 ? 'match' : 'matches'}
            </span>
          </div>
          {result.hits.length === 0 ? (
            <div className="panel">
              <EmptyState
                icon={<SearchX size={20} />}
                title="No captured provenance"
                hint="No recorded session produced this. It may predate Whyso, or was written by hand."
              />
            </div>
          ) : (
            result.hits.map((h, i) => (
              <article key={i} className="panel hit">
                <div className="hit-head">
                  <Link className="title" to={`/sessions/${encodeURIComponent(h.workNodeId)}`}>
                    {titleFromExcerpt(h.prompt, 'Open session')}
                  </Link>
                  <span className="badge accent">{h.matchKind.replace(/_/g, ' ')}</span>
                </div>
                <div className="hit-meta">
                  <span>{formatDateTime(h.startedAt)}</span>
                  <span className="mono">{h.workNodeId}</span>
                </div>
                {h.prompt && (
                  <div>
                    <h3 className="section-title">Prompt</h3>
                    <pre className="pre clamp">{h.prompt}</pre>
                  </div>
                )}
                {h.plan && (
                  <details className="response">
                    <summary>Agent response</summary>
                    <pre className="pre clamp">{h.plan}</pre>
                  </details>
                )}
                {(h.files.length > 0 || h.commits.length > 0) && (
                  <div className="row wrap">
                    {h.commits.map((c) => (
                      <Link
                        key={c}
                        className="badge info mono"
                        to={`/why?mode=commit&sha=${encodeURIComponent(c)}`}
                      >
                        {shortSha(c)}
                      </Link>
                    ))}
                    {h.files.slice(0, 6).map((f) => (
                      <Link
                        key={f}
                        className="badge mono"
                        title={f}
                        to={`/why?mode=file&path=${encodeURIComponent(f)}`}
                      >
                        {f.replace(/\\/g, '/').split('/').pop()}
                      </Link>
                    ))}
                    {h.files.length > 6 && <span className="dim">+{h.files.length - 6} more</span>}
                  </div>
                )}
              </article>
            ))
          )}
        </>
      ) : (
        !error && (
          <div className="panel">
            <EmptyState
              icon={<Waypoints size={20} />}
              title="Nothing traced yet"
              hint="Enter a commit SHA or a file path to see the session and prompt behind it."
            />
          </div>
        )
      )}
    </div>
  )
}
