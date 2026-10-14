import { Link } from 'react-router-dom'
import { Logo } from '../components/Icons'
import { useDocumentTitle } from '../hooks/useDocumentTitle'
import { useAuth } from '../lib/auth'

export function NotFoundPage() {
  useDocumentTitle('Page not found')
  const { isAuthenticated } = useAuth()
  return (
    <div className="center-page">
      <div className="card-narrow">
        <Link to="/" className="brand">
          <Logo />
          <span>RelayHub</span>
        </Link>
        <h1>Page not found</h1>
        <p className="muted">There is nothing at this address. The dashboard lives under /app.</p>
        <div className="row-flex">
          <Link to={isAuthenticated ? '/app/inbox' : '/'} className="btn btn-primary">
            {isAuthenticated ? 'Go to the inbox' : 'Back to the landing page'}
          </Link>
        </div>
      </div>
    </div>
  )
}
