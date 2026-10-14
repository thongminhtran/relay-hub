import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useNavigate } from 'react-router-dom'
import { http, isApiError, readStoredToken, setTokenProvider, setUnauthorizedHandler, TOKEN_STORAGE_KEY } from './api'
import { decodeJwt, isExpired, userFromClaims } from './jwt'
import type { AuthResponse, User } from './types'

function writeStoredToken(token: string | null): void {
  try {
    if (token) localStorage.setItem(TOKEN_STORAGE_KEY, token)
    else localStorage.removeItem(TOKEN_STORAGE_KEY)
  } catch {
    /* private mode / blocked storage: in-memory session only */
  }
}

export interface AuthContextValue {
  token: string | null
  user: User | null
  /** true once `GET /api/v1/auth/me` confirmed the stored token (false while we trust the decoded claims). */
  verified: boolean
  isAuthenticated: boolean
  isOwner: boolean
  isDemo: boolean
  /** Owner-only configuration writes (sources / destinations / routes). Demo workspaces are read-only. */
  canWrite: boolean
  login: (email: string, password: string) => Promise<User>
  loginDemo: () => Promise<User>
  setSession: (auth: AuthResponse) => void
  logout: () => void
}

const AuthContext = createContext<AuthContextValue | null>(null)

interface SessionState {
  token: string | null
  user: User | null
  verified: boolean
}

function initialSession(): SessionState {
  const token = readStoredToken()
  if (!token) return { token: null, user: null, verified: false }
  const claims = decodeJwt(token)
  if (!claims || isExpired(claims)) {
    writeStoredToken(null)
    return { token: null, user: null, verified: false }
  }
  return { token, user: userFromClaims(claims), verified: false }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSessionState] = useState<SessionState>(initialSession)
  const tokenRef = useRef<string | null>(session.token)
  const navigate = useNavigate()

  // While mounted, api.ts reads the token from memory (storage may be blocked); the default falls back to localStorage.
  useEffect(() => {
    setTokenProvider(() => tokenRef.current)
    return () => setTokenProvider(null)
  }, [])

  const logout = useCallback(() => {
    tokenRef.current = null
    writeStoredToken(null)
    setSessionState({ token: null, user: null, verified: false })
  }, [])

  // One 401 on an authenticated call = the session is gone (expired demo token, rotated key, ...).
  useEffect(() => {
    setUnauthorizedHandler(() => {
      if (!tokenRef.current) return
      logout()
      navigate('/login', { replace: true, state: { reason: 'expired' } })
    })
    return () => setUnauthorizedHandler(null)
  }, [logout, navigate])

  const setSession = useCallback((auth: AuthResponse) => {
    tokenRef.current = auth.token
    writeStoredToken(auth.token)
    setSessionState({ token: auth.token, user: auth.user, verified: true })
  }, [])

  // Confirm a restored token with the API (claims may be stale, e.g. renamed workspace).
  useEffect(() => {
    if (!session.token || session.verified) return
    let cancelled = false
    http
      .get<User>('/api/v1/auth/me')
      .then((user) => {
        if (!cancelled) setSessionState((s) => (s.token === session.token ? { ...s, user, verified: true } : s))
      })
      .catch((e: unknown) => {
        // 401/403 = the token is no good; anything else (cold API, offline) keeps the optimistic user.
        if (!cancelled && isApiError(e) && (e.status === 401 || e.status === 403)) logout()
      })
    return () => {
      cancelled = true
    }
  }, [session.token, session.verified, logout])

  const login = useCallback(
    async (email: string, password: string) => {
      const auth = await http.post<AuthResponse>('/api/v1/auth/login', { email, password }, { anonymous: true })
      setSession(auth)
      return auth.user
    },
    [setSession],
  )

  const loginDemo = useCallback(async () => {
    const auth = await http.post<AuthResponse>('/api/v1/auth/demo', {}, { anonymous: true })
    setSession(auth)
    return auth.user
  }, [setSession])

  const value = useMemo<AuthContextValue>(() => {
    const role = session.user?.role ?? null
    const isDemo = session.user?.isDemo ?? false
    return {
      token: session.token,
      user: session.user,
      verified: session.verified,
      isAuthenticated: session.token !== null,
      isOwner: role === 'Owner',
      isDemo,
      canWrite: role === 'Owner' && !isDemo,
      login,
      loginDemo,
      setSession,
      logout,
    }
  }, [session, login, loginDemo, setSession, logout])

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used inside <AuthProvider>')
  return ctx
}
