import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  Background,
  Controls,
  MiniMap,
  ReactFlow,
  Handle,
  Position,
  MarkerType,
  type Edge as RfEdge,
  type Node as RfNode,
  type NodeProps,
} from '@xyflow/react'
import '@xyflow/react/dist/style.css'
import { useSearchParams } from 'react-router-dom'
import { api, type Edge, type NodeDetail, type TreeResponse } from '../api'
import { titleFromExcerpt } from '../format'
import { Inspector } from '../components/Inspector'

function WorkNodeCard({ data, selected }: NodeProps) {
  const d = data as { id: string; excerpt: string; status: string; labels: string[] }
  return (
    <div className={`wt-node${selected ? ' selected' : ''}`}>
      <Handle type="target" position={Position.Left} />
      <div className="id">{d.id}</div>
      <div className="excerpt">{titleFromExcerpt(d.excerpt)}</div>
      <div className="meta">
        <span className={`badge ${d.status === 'active' ? 'warn' : 'ok'}`}>{d.status}</span>
        {d.labels.slice(0, 2).map((l) => (
          <span key={l} className="badge accent">
            {l}
          </span>
        ))}
      </div>
      <Handle type="source" position={Position.Right} />
    </div>
  )
}

const nodeTypes = { worknode: WorkNodeCard }

export function TreeView() {
  const [params, setParams] = useSearchParams()
  const from = params.get('from') ?? undefined
  const [tree, setTree] = useState<TreeResponse | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(from ?? null)
  const [detail, setDetail] = useState<NodeDetail | null>(null)
  const [linkMode, setLinkMode] = useState(false)
  const [linkFrom, setLinkFrom] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const t = await api.tree(from)
      setTree(t)
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [from])

  useEffect(() => {
    void load()
  }, [load])

  useEffect(() => {
    if (!selectedId) {
      setDetail(null)
      return
    }
    let cancelled = false
    api
      .session(selectedId)
      .then((d) => {
        if (!cancelled) setDetail(d)
      })
      .catch(() => {
        if (!cancelled) setDetail(null)
      })
    return () => {
      cancelled = true
    }
  }, [selectedId])

  const { rfNodes, rfEdges } = useMemo(() => {
    if (!tree) return { rfNodes: [] as RfNode[], rfEdges: [] as RfEdge[] }
    const cols = Math.max(1, Math.ceil(Math.sqrt(tree.nodes.length)))
    const nodes: RfNode[] = tree.nodes.map((n, i) => ({
      id: n.id,
      type: 'worknode',
      position: { x: (i % cols) * 260, y: Math.floor(i / cols) * 120 },
      data: {
        id: n.id,
        excerpt: n.promptExcerpt,
        status: n.status,
        labels: n.labels,
      },
    }))
    const edges: RfEdge[] = tree.edges.map((e: Edge) => ({
      id: `${e.from}-${e.to}-${e.kind}`,
      source: e.from,
      target: e.to,
      label: e.label ?? e.kind,
      animated: e.manual,
      style: { stroke: e.manual ? '#2dd4bf' : '#52525b' },
      markerEnd: { type: MarkerType.ArrowClosed, color: e.manual ? '#2dd4bf' : '#52525b' },
    }))
    return { rfNodes: nodes, rfEdges: edges }
  }, [tree])

  return (
    <div className="page full">
      <div className="tree-page" style={{ display: 'grid', gridTemplateColumns: '1fr 320px', minHeight: 0, flex: 1 }}>
        <div style={{ display: 'flex', flexDirection: 'column', minHeight: 0 }}>
          <div className="tree-toolbar">
            <strong>Work Tree</strong>
            <span className="muted">worktree tree{from ? ` --from ${from}` : ''}</span>
            <div style={{ flex: 1 }} />
            <input
              className="inline-input"
              placeholder="Focus from id…"
              defaultValue={from ?? ''}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  const v = (e.target as HTMLInputElement).value.trim()
                  const next = new URLSearchParams(params)
                  if (v) next.set('from', v)
                  else next.delete('from')
                  setParams(next)
                }
              }}
            />
            <button
              type="button"
              className={`btn${linkMode ? ' primary' : ''}`}
              onClick={() => {
                setLinkMode((m) => !m)
                setLinkFrom(null)
              }}
            >
              {linkMode ? 'Link mode on' : 'Link mode'}
            </button>
            <button type="button" className="btn" onClick={() => void load()}>
              Reload
            </button>
          </div>
          {error && <div className="error-banner">{error}</div>}
          <div className="tree-canvas">
            <ReactFlow
              nodes={rfNodes}
              edges={rfEdges}
              nodeTypes={nodeTypes}
              fitView
              onNodeClick={(_e, node) => {
                if (linkMode) {
                  if (!linkFrom) {
                    setLinkFrom(node.id)
                    return
                  }
                  if (linkFrom === node.id) return
                  void (async () => {
                    try {
                      await api.link(linkFrom, node.id)
                      setLinkFrom(null)
                      setLinkMode(false)
                      await load()
                    } catch (err) {
                      setError(err instanceof Error ? err.message : String(err))
                    }
                  })()
                  return
                }
                setSelectedId(node.id)
              }}
            >
              <Background gap={18} color="#27272a" />
              <Controls />
              <MiniMap pannable zoomable />
            </ReactFlow>
          </div>
          {linkMode && (
            <div className="cli-hint" style={{ padding: '8px 12px' }}>
              {linkFrom
                ? `Select target to link from ${linkFrom}`
                : 'Select source node, then target (worktree link)'}
            </div>
          )}
        </div>
        <Inspector
          nodeId={selectedId}
          detail={detail}
          edges={tree?.edges}
          onChanged={() => void load()}
        />
      </div>
    </div>
  )
}
