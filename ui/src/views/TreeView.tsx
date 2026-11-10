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
import dagre from '@dagrejs/dagre'
import { useSearchParams } from 'react-router-dom'
import { CircleAlert, Link2, Network, RefreshCw } from 'lucide-react'
import { api, type NodeDetail, type TreeResponse } from '../api'
import { errorMessage } from '../format'
import { titleFromExcerpt } from '../format'
import { useTheme } from '../theme'
import { StatusBadge } from '../components/Badge'
import { CliHint } from '../components/CliHint'
import { EmptyState } from '../components/EmptyState'
import { Inspector } from '../components/Inspector'

const NODE_W = 240
const NODE_H = 104
const GAP_X = 80
const GAP_Y = 28

interface CardData extends Record<string, unknown> {
  excerpt: string
  status: string
  labels: string[]
  agent: string
  linkSource: boolean
}

function WorkNodeCard({ data, selected }: NodeProps) {
  const d = data as CardData
  return (
    <div className={`wt-node${selected ? ' selected' : ''}${d.linkSource ? ' link-source' : ''}`}>
      <Handle type="target" position={Position.Left} />
      <div className="excerpt">{titleFromExcerpt(d.excerpt)}</div>
      <div className="meta">
        <StatusBadge status={d.status} />
        <span className="badge">{d.agent}</span>
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

/**
 * Linked sessions flow left to right (dagre); sessions with no edges sit in a
 * grid underneath so they don't stretch the graph into one tall column.
 */
function layout(tree: TreeResponse): Map<string, { x: number; y: number }> {
  const pos = new Map<string, { x: number; y: number }>()
  const ids = new Set(tree.nodes.map((n) => n.id))
  const edges = tree.edges.filter((e) => ids.has(e.from) && ids.has(e.to) && e.from !== e.to)
  const linked = new Set(edges.flatMap((e) => [e.from, e.to]))

  let bottom = 0
  if (linked.size > 0) {
    const g = new dagre.graphlib.Graph()
    g.setGraph({ rankdir: 'LR', nodesep: GAP_Y, ranksep: GAP_X })
    g.setDefaultEdgeLabel(() => ({}))
    for (const id of linked) g.setNode(id, { width: NODE_W, height: NODE_H })
    for (const e of edges) g.setEdge(e.from, e.to)
    dagre.layout(g)
    for (const id of linked) {
      const n = g.node(id)
      const y = n.y - NODE_H / 2
      pos.set(id, { x: n.x - NODE_W / 2, y })
      bottom = Math.max(bottom, y + NODE_H)
    }
    bottom += GAP_Y * 3
  }

  const loose = tree.nodes.filter((n) => !linked.has(n.id))
  const cols = Math.max(1, Math.min(4, Math.ceil(Math.sqrt(loose.length))))
  loose.forEach((n, i) => {
    pos.set(n.id, {
      x: (i % cols) * (NODE_W + GAP_X),
      y: bottom + Math.floor(i / cols) * (NODE_H + GAP_Y),
    })
  })
  return pos
}

export function TreeView() {
  const [params, setParams] = useSearchParams()
  const from = params.get('from') ?? undefined
  const theme = useTheme()
  const [tree, setTree] = useState<TreeResponse | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(from ?? null)
  const [detail, setDetail] = useState<NodeDetail | null>(null)
  const [linkMode, setLinkMode] = useState(false)
  const [linkFrom, setLinkFrom] = useState<string | null>(null)
  const [fromInput, setFromInput] = useState(from ?? '')

  const load = useCallback(async () => {
    try {
      const t = await api.tree(from)
      setTree(t)
      setError(null)
    } catch (e) {
      setError(errorMessage(e))
    }
  }, [from])

  useEffect(() => {
    void load()
  }, [load])

  useEffect(() => {
    setFromInput(from ?? '')
  }, [from])

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

  const positions = useMemo(() => (tree ? layout(tree) : null), [tree])

  const { rfNodes, rfEdges } = useMemo(() => {
    if (!tree || !positions) return { rfNodes: [] as RfNode[], rfEdges: [] as RfEdge[] }
    const nodes: RfNode[] = tree.nodes.map((n) => ({
      id: n.id,
      type: 'worknode',
      position: positions.get(n.id) ?? { x: 0, y: 0 },
      selected: n.id === selectedId,
      data: {
        excerpt: n.promptExcerpt,
        status: n.status,
        labels: n.labels,
        agent: n.agent,
        linkSource: n.id === linkFrom,
      } satisfies CardData,
    }))
    const edges: RfEdge[] = tree.edges.map((e) => {
      const color = e.manual ? 'var(--accent)' : 'var(--border-strong)'
      return {
        id: `${e.from}-${e.to}-${e.kind}`,
        source: e.from,
        target: e.to,
        label: e.label ?? e.kind.replace(/_/g, ' '),
        animated: e.manual,
        style: { stroke: color, strokeWidth: e.manual ? 1.75 : 1.25 },
        markerEnd: { type: MarkerType.ArrowClosed, color },
      }
    })
    return { rfNodes: nodes, rfEdges: edges }
  }, [tree, positions, selectedId, linkFrom])

  function applyFrom() {
    const v = fromInput.trim()
    const next = new URLSearchParams(params)
    if (v) next.set('from', v)
    else next.delete('from')
    setParams(next)
  }

  function onNodeClick(nodeId: string) {
    if (!linkMode) {
      setSelectedId(nodeId)
      return
    }
    if (!linkFrom) {
      setLinkFrom(nodeId)
      return
    }
    if (linkFrom === nodeId) return
    void (async () => {
      try {
        await api.link(linkFrom, nodeId)
        setLinkFrom(null)
        setLinkMode(false)
        await load()
      } catch (err) {
        setError(errorMessage(err))
      }
    })()
  }

  return (
    <div className="page full">
      <div className={`tree-page${selectedId ? ' has-selection' : ''}`}>
        <div className="tree-main">
          <div className="tree-toolbar">
            <h1>Work Tree</h1>
            <CliHint command={`whyso tree${from ? ` --from ${from}` : ''}`} />
            <div className="grow" />
            <form
              className="row"
              onSubmit={(e) => {
                e.preventDefault()
                applyFrom()
              }}
            >
              <input
                className="input focus-input"
                placeholder="Focus on session id…"
                aria-label="Focus the tree on a session id"
                value={fromInput}
                onChange={(e) => setFromInput(e.target.value)}
              />
            </form>
            <button
              type="button"
              className={`btn${linkMode ? ' primary' : ''}`}
              aria-pressed={linkMode}
              onClick={() => {
                setLinkMode((m) => !m)
                setLinkFrom(null)
              }}
            >
              <Link2 size={15} />
              {linkMode ? 'Linking…' : 'Link sessions'}
            </button>
            <button
              type="button"
              className="btn icon"
              onClick={() => void load()}
              title="Reload tree"
              aria-label="Reload tree"
            >
              <RefreshCw size={15} />
            </button>
          </div>

          {linkMode && (
            <div className="link-banner" role="status">
              <Link2 size={15} />
              <span className="grow">
                {linkFrom
                  ? 'Now click the session to link to.'
                  : 'Click the session to link from, then the session to link to.'}
              </span>
              <button
                type="button"
                className="btn sm"
                onClick={() => {
                  setLinkMode(false)
                  setLinkFrom(null)
                }}
              >
                Cancel
              </button>
            </div>
          )}

          {error && (
            <div className="banner" role="alert">
              <CircleAlert size={15} />
              <span>{error}</span>
            </div>
          )}

          <div className="tree-canvas">
            {tree && tree.nodes.length === 0 ? (
              <EmptyState
                icon={<Network size={20} />}
                title="No sessions to show"
                hint="The tree fills in as sessions are captured and linked."
                cli="whyso rebuild"
              />
            ) : (
              <ReactFlow
                // Re-fit when the set of nodes changes (new focus, reload).
                key={`${from ?? ''}:${rfNodes.length}`}
                nodes={rfNodes}
                edges={rfEdges}
                nodeTypes={nodeTypes}
                colorMode={theme.resolved}
                fitView
                fitViewOptions={{ padding: 0.15, maxZoom: 1 }}
                minZoom={0.1}
                nodesConnectable={false}
                onNodeClick={(_e, node) => onNodeClick(node.id)}
                onPaneClick={() => {
                  if (!linkMode) setSelectedId(null)
                }}
              >
                <Background gap={20} />
                <Controls showInteractive={false} />
                <MiniMap pannable zoomable />
              </ReactFlow>
            )}
          </div>
        </div>
        <Inspector
          nodeId={selectedId}
          detail={detail}
          edges={tree?.edges}
          onClose={() => setSelectedId(null)}
          onChanged={() => void load()}
        />
      </div>
    </div>
  )
}
