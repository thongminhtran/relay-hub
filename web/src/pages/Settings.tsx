import { Navigate, NavLink, Outlet, useLocation } from 'react-router-dom'
import { useDocumentTitle } from '../hooks/useDocumentTitle'
import { useDestinations, useRoutes, useSources } from '../hooks/useData'

const TABS = [
  { to: 'sources', label: 'Sources' },
  { to: 'destinations', label: 'Destinations' },
  { to: 'routes', label: 'Routes' },
] as const

export function SettingsPage() {
  const { pathname } = useLocation()
  const current = TABS.find((t) => pathname.endsWith(`/${t.to}`))
  useDocumentTitle(current ? `${current.label} · Settings` : 'Settings')
  const sources = useSources()
  const destinations = useDestinations()
  const routes = useRoutes()
  const counts: Record<string, number | undefined> = { sources: sources.data?.length, destinations: destinations.data?.length, routes: routes.data?.length }

  if (!current) return <Navigate to="/app/settings/sources" replace />

  return (
    <>
      <header className="page-head">
        <div>
          <h1>Settings</h1>
          <p className="desc">Sources feed messages in, destinations receive notifications, routes connect the two with a template.</p>
        </div>
      </header>
      <nav className="tabs" aria-label="Settings sections">
        {TABS.map((t) => (
          <NavLink key={t.to} to={`/app/settings/${t.to}`} className="tab" end>
            {t.label}
            {counts[t.to] !== undefined ? <span className="tb num">{counts[t.to]}</span> : null}
          </NavLink>
        ))}
      </nav>
      <Outlet />
    </>
  )
}
