import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { api, type SearchHit } from '../api'

export function SearchView() {
  const [params, setParams] = useSearchParams()
  const [q, setQ] = useState(params.get('q') ?? '')
  const [type, setType] = useState(params.get('type') ?? '')
  const [from, setFrom] = useState(params.get('from') ?? '')
  const [hits, setHits] = useState<SearchHit[]>([])
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    setQ(params.get('q') ?? '')
    setType(params.get('type') ?? '')
    setFrom(params.get('from') ?? '')
  }, [params])

  useEffect(() => {
    const query = params.get('q')
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
        if (!cancelled) {
          setHits(h)
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
    if (q.trim()) next.set('q', q.trim())
    if (type.trim()) next.set('type', type.trim())
    if (from.trim()) next.set('from', from.trim())
    setParams(next)
  }

  return (
    <div className="page">
      <div className="page-header">
        <div>
          <h1>Search</h1>
          <p>Search prompts, files, commits, activities — <code>whyso search</code>.</p>
        </div>
      </div>

      <div className="toolbar">
        <div className="field" style={{ flex: 1 }}>
          <label htmlFor="search-q">Query</label>
          <input
            id="search-q"
            value={q}
            onChange={(e) => setQ(e.target.value)}
            placeholder="auth, sess_, …"
            onKeyDown={(e) => e.key === 'Enter' && run()}
          />
        </div>
        <div className="field">
          <label htmlFor="search-type">--type</label>
          <input
            id="search-type"
            value={type}
            onChange={(e) => setType(e.target.value)}
            placeholder="file | prompt | …"
          />
        </div>
        <div className="field">
          <label htmlFor="search-from">--from</label>
          <input
            id="search-from"
            value={from}
            onChange={(e) => setFrom(e.target.value)}
            placeholder="commit:sha | node:id"
          />
        </div>
        <button type="button" className="btn primary" onClick={run} style={{ alignSelf: 'end' }}>
          Search
        </button>
      </div>

      <div className="cli-hint" style={{ marginBottom: 12 }}>
        whyso search {q || '<query>'}
        {type ? ` --type ${type}` : ''}
        {from ? ` --from ${from}` : ''}
      </div>

      {error && <div className="error-banner">{error}</div>}
      {loading && <div className="muted">Searching…</div>}

      <div className="panel">
        <div className="panel-head">
          <span>Results</span>
          <span>{hits.length}</span>
        </div>
        {hits.length === 0 && !loading ? (
          <div className="empty">
            {params.get('q') ? 'No hits' : 'Enter a query'}
            <br />
            <code>whyso search &quot;…&quot;</code>
          </div>
        ) : (
          <div className="table-wrap">
            <table className="data">
              <thead>
                <tr>
                  <th>Node</th>
                  <th>Field</th>
                  <th>Snippet</th>
                </tr>
              </thead>
              <tbody>
                {hits.map((h, i) => (
                  <tr key={`${h.workNodeId}-${h.field}-${i}`}>
                    <td className="mono">
                      <Link to={`/sessions/${encodeURIComponent(h.workNodeId)}`}>{h.workNodeId}</Link>
                    </td>
                    <td>
                      <span className="badge">{h.field}</span>
                    </td>
                    <td>
                      <pre className="pre" style={{ maxHeight: 120, overflow: 'auto' }}>
                        {h.snippet}
                      </pre>
                    </td>
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
