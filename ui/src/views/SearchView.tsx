import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { CircleAlert, Search, SearchX } from 'lucide-react'
import { api, type SearchHit } from '../api'
import { CliHint } from '../components/CliHint'
import { EmptyState } from '../components/EmptyState'
import { SkeletonRows } from '../components/Skeleton'

function highlight(text: string, query: string): ReactNode {
  const terms = query
    .split(/\s+/)
    .map((t) => t.replace(/[^\p{L}\p{N}_-]/gu, ''))
    .filter((t) => t.length > 1)
  if (terms.length === 0) return text
  // Terms are reduced to letters, digits, "_" and "-", so they need no regex escaping.
  const parts = text.split(new RegExp(`(${terms.join('|')})`, 'giu'))
  return parts.map((part, i) => (i % 2 === 1 ? <mark key={i}>{part}</mark> : part))
}

export function SearchView() {
  const [params, setParams] = useSearchParams()
  const [q, setQ] = useState(params.get('q') ?? '')
  const [type, setType] = useState(params.get('type') ?? '')
  const [from, setFrom] = useState(params.get('from') ?? '')
  const [hits, setHits] = useState<SearchHit[]>([])
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const activeQuery = params.get('q')?.trim() ?? ''

  useEffect(() => {
    setQ(params.get('q') ?? '')
    setType(params.get('type') ?? '')
    setFrom(params.get('from') ?? '')
  }, [params])

  useEffect(() => {
    const query = params.get('q')
    setError(null)
    if (!query?.trim()) {
      setHits([])
      return
    }
    let cancelled = false
    setLoading(true)
    api
      .search(query, {
        type: params.get('type') || undefined,
        from: params.get('from') || undefined,
      })
      .then((h) => {
        if (!cancelled) setHits(h)
      })
      .catch((e: Error) => {
        if (!cancelled) {
          setHits([])
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
    if (q.trim()) next.set('q', q.trim())
    if (type.trim()) next.set('type', type.trim())
    if (from.trim()) next.set('from', from.trim())
    setParams(next)
  }

  const cli = useMemo(
    () =>
      `whyso search ${q ? JSON.stringify(q) : '<query>'}${type ? ` --type ${type}` : ''}${from ? ` --from ${from}` : ''}`,
    [q, type, from],
  )

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h1>Search</h1>
          <p>Search prompts, sessions, files, commits and activities.</p>
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
        <div className="field grow">
          <label htmlFor="search-q">Query</label>
          <div className="input-icon">
            <Search size={15} />
            <input
              id="search-q"
              className="input"
              autoFocus
              value={q}
              onChange={(e) => setQ(e.target.value)}
              placeholder="auth, a file name, a session id…"
            />
          </div>
        </div>
        <div className="field narrow">
          <label htmlFor="search-type">Type (optional)</label>
          <input
            id="search-type"
            value={type}
            onChange={(e) => setType(e.target.value)}
            placeholder="file, prompt…"
          />
        </div>
        <div className="field">
          <label htmlFor="search-from">Starting from (optional)</label>
          <input
            id="search-from"
            className="mono"
            value={from}
            onChange={(e) => setFrom(e.target.value)}
            placeholder="commit:<sha> or node:<id>"
          />
        </div>
        <button type="submit" className="btn primary">
          <Search size={15} />
          Search
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
          <SkeletonRows rows={5} />
        </div>
      ) : hits.length === 0 ? (
        !error && (
          <div className="panel">
            {activeQuery ? (
              <EmptyState
                icon={<SearchX size={20} />}
                title={`No results for “${activeQuery}”`}
                hint="Try fewer or different words, or remove the type filter."
              />
            ) : (
              <EmptyState
                icon={<Search size={20} />}
                title="Search your captured history"
                hint="Find the sessions where something was asked, changed or committed."
              />
            )}
          </div>
        )
      ) : (
        <>
          <div className="result-head">
            <span className="dim">
              {hits.length} {hits.length === 1 ? 'result' : 'results'}
            </span>
          </div>
          <div className="panel">
            {hits.map((h, i) => (
              <Link
                key={`${h.workNodeId}-${h.field}-${i}`}
                className="search-hit"
                to={`/sessions/${encodeURIComponent(h.workNodeId)}`}
              >
                <div className="row">
                  <span className="badge">{h.field}</span>
                  <span className="mono dim truncate">{h.workNodeId}</span>
                </div>
                <div className="snippet">{highlight(h.snippet, activeQuery)}</div>
              </Link>
            ))}
          </div>
        </>
      )}
    </div>
  )
}
