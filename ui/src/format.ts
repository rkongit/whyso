export function shortSha(sha?: string | null, len = 7): string {
  if (!sha) return '—'
  return sha.length <= len ? sha : sha.slice(0, len)
}

export function formatDuration(seconds?: number | null): string {
  if (seconds == null || Number.isNaN(seconds)) return '—'
  const s = Math.max(0, Math.round(seconds))
  if (s < 60) return `${s}s`
  const m = Math.floor(s / 60)
  const rem = s % 60
  if (m < 60) return `${m}m ${rem}s`
  const h = Math.floor(m / 60)
  return `${h}h ${m % 60}m`
}

export function formatTime(iso?: string | null): string {
  if (!iso) return ''
  const d = new Date(iso)
  return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', hour12: false })
}

export function formatDay(iso: string): string {
  const d = new Date(iso)
  return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' })
}

export function formatRelativeTime(iso?: string | null): string {
  if (!iso) return ''
  const then = new Date(iso).getTime()
  if (Number.isNaN(then)) return ''
  const s = Math.round((Date.now() - then) / 1000)
  if (s < 45) return 'just now'
  if (s < 3600) return `${Math.max(1, Math.round(s / 60))}m ago`
  if (s < 86400) return `${Math.round(s / 3600)}h ago`
  if (s < 7 * 86400) return `${Math.round(s / 86400)}d ago`
  return formatDay(iso)
}

export function formatDateTime(iso?: string | null): string {
  if (!iso) return ''
  return `${formatDay(iso)}, ${formatTime(iso)}`
}

export function dayKey(iso: string): string {
  const d = new Date(iso)
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

export function titleFromExcerpt(excerpt?: string | null, fallback = 'Untitled work'): string {
  const t = (excerpt ?? '').trim()
  if (!t) return fallback
  const first = t.split(/\r?\n/)[0]!.trim()
  return first.length > 72 ? `${first.slice(0, 72)}…` : first
}

export function basename(path: string): string {
  const parts = path.replace(/\\/g, '/').split('/')
  return parts[parts.length - 1] || path
}

export function errorMessage(e: unknown): string {
  return e instanceof Error ? e.message : String(e)
}
