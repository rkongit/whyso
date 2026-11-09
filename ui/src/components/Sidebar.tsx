import { NavLink } from 'react-router-dom'
import { useAppData } from '../AppData'
import worktreeIcon from '../assets/worktree-icon.png'

const NAV = [
  {
    group: 'WORK',
    items: [
      { to: '/', label: 'Overview', end: true, glyph: '◉' },
      { to: '/tree', label: 'Work Tree', glyph: '⎇' },
      { to: '/sessions', label: 'Sessions', glyph: '☰' },
    ],
  },
  {
    group: 'TRACE',
    items: [
      { to: '/why', label: 'Why', glyph: '?' },
      { to: '/search', label: 'Search', glyph: '/' },
    ],
  },
] as const

export function Sidebar() {
  const { status } = useAppData()
  return (
    <aside className="sidebar">
      <div className="brand">
        <img className="brand-mark" src={worktreeIcon} alt="Worktree" width={28} height={28} />
        <span>Worktree</span>
      </div>

      {NAV.map((g) => (
        <div key={g.group}>
          <div className="nav-label">{g.group}</div>
          {g.items.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={'end' in item ? item.end : false}
              className={({ isActive }) => `nav-item${isActive ? ' active' : ''}`}
            >
              <span className="glyph">{item.glyph}</span>
              <span>{item.label}</span>
            </NavLink>
          ))}
        </div>
      ))}

      <div className="sidebar-foot">
        {status ? (
          <>
            <div className="mono">{status.sessionCount} sessions</div>
            <div className="mono">{status.promptCount} prompts</div>
          </>
        ) : (
          <div>Loading…</div>
        )}
      </div>
    </aside>
  )
}
