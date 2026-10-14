import { Navigate, Outlet, useLocation } from 'react-router-dom'
import { useAuth } from '../lib/auth'

/** Route guard: no token → /login (remembering where the user wanted to go). */
export function RequireAuth() {
  const { isAuthenticated } = useAuth()
  const location = useLocation()
  if (!isAuthenticated) {
    return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />
  }
  return <Outlet />
}
