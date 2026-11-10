/** Session status: "active" is in progress, anything else is treated as finished. */
export function StatusBadge({ status }: { status?: string | null }) {
  if (!status) return <span className="badge">—</span>
  return (
    <span className={`badge ${status === 'active' ? 'warn' : 'ok'}`}>
      <span className="dot" />
      {status}
    </span>
  )
}
