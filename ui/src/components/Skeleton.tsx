const WIDTHS = ['72%', '54%', '86%', '61%', '78%', '48%']

/** Placeholder lines shown while data loads. */
export function SkeletonRows({ rows = 4 }: { rows?: number }) {
  return (
    <div className="skeleton-rows" aria-busy="true" aria-label="Loading">
      {Array.from({ length: rows }, (_, i) => (
        <span key={i} className="skeleton" style={{ width: WIDTHS[i % WIDTHS.length] }} />
      ))}
    </div>
  )
}
