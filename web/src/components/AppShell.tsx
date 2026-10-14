import type { ComponentType, SVGProps } from 'react'
import { Link, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { API_BASE } from '../lib/api'
import { useAuth } from '../lib/auth'
import { initials } from '../lib/format'
import { IconBolt, IconChart, IconChat, IconDocs, IconInbox, IconLogout, IconSettings, Logo } from './Icons'
import { ThemeToggle } from './ThemeToggle'

interface NavItemDef {
  to: string
  label: string
  icon: ComponentType<SVGProps<SVGSVGElement>>
  /** Extra path prefixes that count as "this section" (event detail belongs to the inbox). */
  also?: string[]
}

const NAV: NavItemDef[] = [
  { to: '/app/inbox', label: 'Inbox', icon: IconInbox, also: ['/app/events'] },
  { to: '/app/conversations', label: 'Conversations', icon: IconChat },
  { to: '/app/stats', label: 'Stats', icon: IconChart },
  { to: '/app/settings', label: 'Settings', icon: IconSettings },
]
/** Only for Demo-workspace users (the API answers 403 demo-only for everyone else). */
const DEMO_NAV: NavItemDef = { to: '/app/demo', label: 'Demo', icon: IconBolt }

function isSection(pathname: string, item: NavItemDef): boolean {
  return [item.to, ...(item.also ?? [])].some((p) => pathname === p || pathname.startsWith(p + '/'))
}

function NavItem({ item, pathname }: { item: NavItemDef; pathname: string }) {
  const Icon = item.icon
  return (
    <Link to={item.to} aria-current={isSection(pathname, item) ? 'page' : undefined}>
      <Icon />
      <span>{item.label}</span>
    </Link>
  )
}

export function AppShell() {
  const { user, logout } = useAuth()
  const navigate = useNavigate()
  const { pathname } = useLocation()

  const signOut = () => {
    logout()
    navigate('/', { replace: true })
  }

  const roleLine = user ? `${user.role}${user.isDemo ? ' · demo workspace' : ''}` : ''
  const nav = user?.isDemo ? [...NAV, DEMO_NAV] : NAV

  return (
    <div className="app">
      <aside className="rail" aria-label="Sidebar">
        <Link to="/" className="brand">
          <Logo />
          <span>RelayHub</span>
        </Link>
        <div className="ws">
          <b title={user?.workspaceName}>{user?.workspaceName || 'Workspace'}</b>
          <span>{user?.isDemo ? <span className="badge">Demo</span> : 'Workspace'}</span>
        </div>
        <nav className="nav" aria-label="Main">
          {nav.map((item) => (
            <NavItem key={item.to} item={item} pathname={pathname} />
          ))}
          <a href={`${API_BASE}/docs`} target="_blank" rel="noreferrer">
            <IconDocs />
            <span>API docs</span>
          </a>
        </nav>
        <div className="rail-foot">
          <div className="who">
            <span className="avatar" aria-hidden="true">
              {initials(user?.email?.split('@')[0] ?? user?.role, 'RH')}
            </span>
            <div>
              <b title={user?.email}>{user?.email || 'Signed in'}</b>
              <span>{roleLine}</span>
            </div>
          </div>
          <div className="rail-btns">
            <ThemeToggle className="btn btn-sm" />
            <button type="button" className="btn btn-sm" onClick={signOut}>
              <IconLogout />
              Sign out
            </button>
          </div>
        </div>
      </aside>

      <header className="topbar">
        <Link to="/" className="brand">
          <Logo />
          <span>RelayHub</span>
        </Link>
        {user?.isDemo ? <span className="badge">Demo</span> : null}
        <span className="spacer" />
        <ThemeToggle className="btn btn-sm btn-quiet btn-icon" />
        <button type="button" className="btn btn-sm btn-quiet btn-icon" onClick={signOut} aria-label="Sign out" title="Sign out">
          <IconLogout />
        </button>
      </header>

      <main className="main" id="main">
        <Outlet />
      </main>

      <nav className="tabbar" aria-label="Main">
        {nav.map((item) => (
          <NavItem key={item.to} item={item} pathname={pathname} />
        ))}
      </nav>
    </div>
  )
}
