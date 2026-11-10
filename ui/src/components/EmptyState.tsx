import type { ReactNode } from 'react'
import { CliHint } from './CliHint'

interface Props {
  icon?: ReactNode
  title: string
  hint?: ReactNode
  cli?: string
  compact?: boolean
}

export function EmptyState({ icon, title, hint, cli, compact }: Props) {
  return (
    <div className={`empty-state${compact ? ' compact' : ''}`}>
      {icon && <div className="icon">{icon}</div>}
      <div className="title">{title}</div>
      {hint && <div className="hint">{hint}</div>}
      {cli && <CliHint command={cli} />}
    </div>
  )
}
