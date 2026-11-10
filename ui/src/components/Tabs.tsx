export interface TabItem<T extends string> {
  id: T
  label: string
  count?: number
}

interface Props<T extends string> {
  tabs: TabItem<T>[]
  value: T
  onChange: (id: T) => void
}

export function Tabs<T extends string>({ tabs, value, onChange }: Props<T>) {
  return (
    <div
      className="tabs"
      role="tablist"
      onKeyDown={(e) => {
        if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return
        e.preventDefault()
        const i = tabs.findIndex((t) => t.id === value)
        const next = tabs[(i + (e.key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length]!
        onChange(next.id)
        const buttons = e.currentTarget.querySelectorAll<HTMLButtonElement>('[role="tab"]')
        buttons[tabs.indexOf(next)]?.focus()
      }}
    >
      {tabs.map((t) => (
        <button
          key={t.id}
          type="button"
          role="tab"
          className="tab"
          aria-selected={t.id === value}
          tabIndex={t.id === value ? 0 : -1}
          onClick={() => onChange(t.id)}
        >
          {t.label}
          {t.count != null && t.count > 0 && <span className="count">{t.count}</span>}
        </button>
      ))}
    </div>
  )
}
