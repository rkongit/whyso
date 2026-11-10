import { NavLink } from 'react-router-dom'
import { History, LayoutDashboard, Network, Search, Waypoints, type LucideIcon } from 'lucide-react'
import { useAppData } from '../AppData'
import whysoIcon from '../assets/whyso-icon.png'

interface NavItem {
  to: string
  label: string
  icon: LucideIcon
  end?: boolean
}

const NAV: { group: string; items: NavItem[] }[] = [
  {
    group: 'Work',
    items: [
      { to: '/', label: 'Overview', icon: LayoutDashboard, end: true },
      { to: '/tree', label: 'Work Tree', icon: Network },
      { to: '/sessions', label: 'Sessions', icon: History },
    ],
  },
  {
    group: 'Trace',
    items: [
      { to: '/why', label: 'Why', icon: Waypoints },
      { to: '/search', label: 'Search', icon: Search },
    ],
  },
]

export function Sidebar() {
  const { status } = useAppData()
  return (
    <aside className="sidebar">
      <div className="brand">
        <img className="brand-mark" src={whysoIcon} alt="" width={26} height={26} />
        <span>Whyso</span>
      </div>

      {NAV.map((g) => (
        <nav key={g.group} className="nav-group" aria-label={g.group}>
          <div className="nav-label">{g.group}</div>
          {g.items.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.end ?? false}
              title={item.label}
              className={({ isActive }) => `nav-item${isActive ? ' active' : ''}`}
            >
              <item.icon size={17} />
              <span>{item.label}</span>
            </NavLink>
          ))}
        </nav>
      ))}

      <div className="sidebar-foot">
        <div className="foot-stat">
          <div className="n">{status?.sessionCount ?? '–'}</div>
          <div className="l">sessions</div>
        </div>
        <div className="foot-stat">
          <div className="n">{status?.promptCount ?? '–'}</div>
          <div className="l">prompts</div>
        </div>
      </div>
    </aside>
  )
}
