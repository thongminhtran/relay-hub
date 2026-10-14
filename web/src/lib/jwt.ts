import type { Role, User } from './types'

/** Claims the API puts in the JWT (see contract: sub, email, role, ws, wsn, demo, exp). */
export interface JwtClaims {
  sub?: string
  email?: string
  role?: string
  ws?: string | number
  wsn?: string
  demo?: string | boolean
  exp?: number
}

function base64UrlDecode(s: string): string {
  const b64 = s.replace(/-/g, '+').replace(/_/g, '/')
  const padded = b64 + '='.repeat((4 - (b64.length % 4)) % 4)
  const bin = atob(padded)
  const bytes = Uint8Array.from(bin, (c) => c.charCodeAt(0))
  return new TextDecoder().decode(bytes)
}

/** Decode the payload of a JWT without verifying it (the API verifies; this is only for instant hydration). */
export function decodeJwt(token: string): JwtClaims | null {
  const parts = token.split('.')
  if (parts.length !== 3) return null
  try {
    const payload: unknown = JSON.parse(base64UrlDecode(parts[1]))
    return typeof payload === 'object' && payload !== null ? (payload as JwtClaims) : null
  } catch {
    return null
  }
}

export function isExpired(claims: JwtClaims, nowMs = Date.now()): boolean {
  return typeof claims.exp === 'number' && claims.exp * 1000 <= nowMs
}

/** Best-effort user from claims; replaced by `GET /api/v1/auth/me` once it answers. */
export function userFromClaims(claims: JwtClaims): User | null {
  const role = claims.role === 'Owner' || claims.role === 'Demo' ? (claims.role as Role) : null
  if (!role) return null
  return {
    id: Number(claims.sub ?? 0),
    email: claims.email ?? '',
    role,
    workspaceId: Number(claims.ws ?? 0),
    workspaceName: claims.wsn ?? '',
    isDemo: claims.demo === true || claims.demo === 'true' || role === 'Demo',
  }
}
