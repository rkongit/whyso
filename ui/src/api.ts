export interface ProjectInfo {
  projectId: string
  root: string
  sessionCount: number
}

export interface StatusInfo {
  projectId: string
  root: string
  bind: string
  daemonStatus: string
  daemonRunning: boolean
  sessionCount: number
  promptCount: number
}

/** Session list row (enriched projection). Also used for tree nodes. */
export interface SessionInfo {
  id: string
  projectId?: string
  sessionId?: string
  agent: string
  startedAt: string
  endedAt?: string | null
  status: string
  gitHead?: string | null
  gitHeadAfter?: string | null
  promptExcerpt: string
  turnCount: number
  fileCount: number
  commandCount: number
  testCount: number
  commitCount: number
  labels: string[]
  topFiles?: string[]
  commitShas?: string[]
  incomingEdges?: number
  outgoingEdges?: number
}

/** @deprecated alias — tree API still returns this shape */
export type NodeListItem = SessionInfo

export interface FileTouch {
  sessionId: string
  path: string
  action: string
  at: string
  promptExcerpt?: string | null
  commitShas: string[]
}

export interface LineRange {
  startLine: number
  endLine: number
}

export interface FileArtifact {
  path: string
  action: string
  ranges: LineRange[]
  additions?: number | null
  deletions?: number | null
}

export interface TestResult {
  name?: string | null
  suite?: string | null
  status?: string | null
  passed?: number | null
  failed?: number | null
  phase: string
  startedAt?: string | null
  endedAt?: string | null
}

export interface Edge {
  from: string
  to: string
  kind: string
  label?: string | null
  score?: number | null
  manual: boolean
}

export interface Activity {
  kind: string
  summary: string
  timestamp?: string | null
  name?: string | null
  path?: string | null
  action?: string | null
  command?: string | null
  phase?: string | null
  status?: string | null
  passed?: number | null
  failed?: number | null
  durationMs?: number | null
  errorMessage?: string | null
  edits?: unknown
  output?: unknown
}

export interface Turn {
  prompt: string
  plan?: string | null
  startedAt: string
  generationId?: string | null
  actions: Activity[]
  files: string[]
  commits: string[]
}

export interface AfterwardsNode {
  id: string
  promptExcerpt: string
  startedAt: string
  status: string
  via?: string | null
}

export interface NodeDetail {
  id: string
  sessionId: string
  projectId: string
  agent: string
  startedAt: string
  endedAt?: string | null
  durationSeconds?: number | null
  status: string
  gitHead?: string | null
  gitHeadAfter?: string | null
  prompts: string[]
  responses: string[]
  files: FileArtifact[]
  commands: string[]
  tests: TestResult[]
  commits: string[]
  labels: string[]
  edges: Edge[]
  turns: Turn[]
  afterwards: AfterwardsNode[]
  activityCount: number
}

export type SessionDetail = NodeDetail

export interface FileDetail {
  path: string
  action: string
  ranges: LineRange[]
  edits?: unknown
  snippet?: string | null
  additions?: number | null
  deletions?: number | null
}

export interface SearchHit {
  workNodeId: string
  snippet: string
  field: string
}

export interface TreeResponse {
  nodes: SessionInfo[]
  edges: Edge[]
}

export interface ProvenanceHit {
  sessionId: string
  workNodeId: string
  matchKind: string
  prompt: string
  plan?: string | null
  startedAt: string
  generationId?: string | null
  files: string[]
  commits: string[]
}

export interface ProvenanceResult {
  subjectKind: string
  sha?: string | null
  path?: string | null
  startLine?: number | null
  endLine?: number | null
  hits: ProvenanceHit[]
}

export interface ActivityTimeline {
  sessionId: string
  turns: Turn[]
}

export interface RebuildResult {
  nodeCount: number
}

async function parseError(res: Response): Promise<string> {
  const body = await res.text()
  try {
    const j = JSON.parse(body) as { error?: string }
    if (j.error) return j.error
  } catch {
    // ignore
  }
  return body || `${res.status} ${res.statusText}`
}

async function getJson<T>(path: string): Promise<T> {
  const res = await fetch(path)
  if (!res.ok) throw new Error(await parseError(res))
  return res.json() as Promise<T>
}

async function sendJson<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(path, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (!res.ok) throw new Error(await parseError(res))
  return res.json() as Promise<T>
}

function encodeFilePath(path: string): string {
  return path
    .split('/')
    .map(encodeURIComponent)
    .join('/')
}

export const api = {
  project: () => getJson<ProjectInfo>('/api/v1/project'),
  status: () => getJson<StatusInfo>('/api/v1/status'),
  sessions: (opts?: { file?: string; prefix?: string }) => {
    const q = new URLSearchParams()
    if (opts?.file) q.set('file', opts.file)
    if (opts?.prefix) q.set('prefix', opts.prefix)
    const qs = q.toString()
    return getJson<SessionInfo[] | FileTouch[]>(`/api/v1/sessions${qs ? `?${qs}` : ''}`)
  },
  session: (id: string) => getJson<SessionDetail>(`/api/v1/sessions/${encodeURIComponent(id)}`),
  events: (id: string) =>
    getJson<unknown[]>(`/api/v1/sessions/${encodeURIComponent(id)}/events`),
  activity: (id: string) =>
    getJson<ActivityTimeline>(`/api/v1/sessions/${encodeURIComponent(id)}/activity`),
  file: (id: string, path: string) =>
    getJson<FileDetail>(
      `/api/v1/sessions/${encodeURIComponent(id)}/files/${encodeFilePath(path)}`,
    ),
  tree: (from?: string) =>
    getJson<TreeResponse>(
      from ? `/api/v1/tree?from=${encodeURIComponent(from)}` : '/api/v1/tree',
    ),
  search: (q: string, opts?: { type?: string; from?: string }) => {
    const params = new URLSearchParams({ q })
    if (opts?.type) params.set('type', opts.type)
    if (opts?.from) params.set('from', opts.from)
    return getJson<SearchHit[]>(`/api/v1/search?${params}`)
  },
  whyCommit: (sha: string) =>
    getJson<ProvenanceResult>(`/api/v1/why/commit/${encodeURIComponent(sha)}`),
  whyFile: (path: string, lines?: string) => {
    const params = new URLSearchParams({ path })
    if (lines) params.set('lines', lines)
    return getJson<ProvenanceResult>(`/api/v1/why/file?${params}`)
  },
  link: (from: string, to: string, label?: string) =>
    sendJson<Edge>('POST', '/api/v1/edges', { from, to, label }),
  unlink: (from: string, to: string, kind = 'manual') => {
    const params = new URLSearchParams({ from, to, kind })
    return sendJson<{ ok: boolean }>('DELETE', `/api/v1/edges?${params}`)
  },
  addLabel: (id: string, label: string) =>
    sendJson<{ ok: boolean }>('POST', `/api/v1/sessions/${encodeURIComponent(id)}/labels`, {
      label,
    }),
  removeLabel: (id: string, label: string) =>
    sendJson<{ ok: boolean }>(
      'DELETE',
      `/api/v1/sessions/${encodeURIComponent(id)}/labels/${encodeURIComponent(label)}`,
    ),
  rebuild: () => sendJson<RebuildResult>('POST', '/api/v1/rebuild'),
}
