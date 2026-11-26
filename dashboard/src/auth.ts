/*
 * Sign-in against the corporate directory (CorpDirectory.Api).
 *
 * The directory checks the password and returns a signed access token for this app
 * ("synchub"), carrying the user's roles. Every API call sends it as a Bearer token;
 * the Support API verifies the signature and enforces the roles.
 *
 * The token is kept in sessionStorage, so it is gone when the tab closes. A production
 * app would use MSAL (or a backend-for-frontend with an HttpOnly cookie) instead, because
 * anything in browser storage can be read by a script injected into the page.
 */

import { reportApiUnreachable } from './telemetry'

export const DIRECTORY_URL =
  (import.meta.env.VITE_DIRECTORY_URL as string | undefined) ?? 'http://localhost:5090'

const RESOURCE = 'synchub'
const STORAGE_KEY = 'synchub.session'

export interface SessionUser {
  userId: string
  displayName: string
  userPrincipalName: string
  jobTitle: string | null
  roles: string[]
}

export interface Session {
  accessToken: string
  expiresAtUtc: string
  user: SessionUser
}

export class AuthError extends Error {
  constructor(public code: string, message: string) {
    super(message)
  }
}

const expiry = (s: Session) => new Date(/Z$|[+-]\d\d:\d\d$/.test(s.expiresAtUtc) ? s.expiresAtUtc : `${s.expiresAtUtc}Z`)

/**
 * The saved session from sessionStorage, or null if there is none or it has expired.
 * Kept in sessionStorage, not localStorage, so it ends when the tab closes.
 */
export function loadSession(): Session | null {
  try {
    const raw = window.sessionStorage.getItem(STORAGE_KEY)
    if (!raw) return null
    const session = JSON.parse(raw) as Session
    return expiry(session).getTime() > Date.now() ? session : null
  } catch {
    return null
  }
}

/**
 * Saves the session, or clears it when given null. Ignores storage being unavailable.
 * @param session The session from sign-in, or null to sign out.
 */
export function saveSession(session: Session | null) {
  try {
    if (session) window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify(session))
    else window.sessionStorage.removeItem(STORAGE_KEY)
  } catch {
    // storage unavailable: the session lasts until the page reloads
  }
  current = session
}

let current: Session | null = loadSession()

/** The access token to send as a Bearer token, or null if signed out or expired. */
export function currentToken(): string | null {
  if (current && expiry(current).getTime() <= Date.now()) current = null
  return current?.accessToken ?? null
}

async function post<T>(path: string, body: unknown): Promise<T> {
  let response: Response
  try {
    response = await fetch(`${DIRECTORY_URL}${path}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
  } catch {
    reportApiUnreachable('directory-api', path)   // the path only: the body holds the password
    throw new AuthError('unreachable', `Can't reach the directory at ${DIRECTORY_URL}. Is CorpDirectory.Api running?`)
  }
  const text = await response.text()
  if (!response.ok) {
    let code = 'error'
    let message = `${response.status} ${response.statusText}`
    try {
      const b = JSON.parse(text) as { error?: string; message?: string }
      code = b.error ?? code
      message = b.message ?? message
    } catch {
      // not JSON
    }
    throw new AuthError(code, message)
  }
  return (text ? JSON.parse(text) : undefined) as T
}

/**
 * Signs in against the corporate directory and saves the session.
 * @param username Full UPN or just "first.last".
 * @param password The user's password; sent only to the directory, never stored.
 * @returns The session, including the token and the user's roles for this app.
 * @throws AuthError with a code such as invalid_grant (wrong password), account_locked, account_disabled,
 * not_assigned (no role for this app), password_change_required or unreachable.
 */
export async function signIn(username: string, password: string): Promise<Session> {
  const session = await post<Session>('/api/auth/token', { username, password, resource: RESOURCE })
  saveSession(session)
  return session
}

/**
 * Changes the user's password at the directory, e.g. when a temporary password must be replaced.
 * @throws AuthError: invalid_grant if the current password is wrong, weak_password if the new one breaks the policy.
 */
export function changePassword(username: string, currentPassword: string, newPassword: string) {
  return post<void>('/api/auth/change-password', { username, currentPassword, newPassword })
}

/* ---------------- Roles ---------------- */

export const ROLE_LABELS: Record<string, string> = {
  'SyncHub.Viewer': 'Viewer',
  'SyncHub.Support': 'Support Analyst',
  'SyncHub.DataSteward': 'Data Steward',
  'SyncHub.Admin': 'Administrator',
}

export interface Permissions {
  fixExceptions: boolean
  stewardData: boolean
}

/**
 * What the signed-in user may do, from their roles, so the UI can hide buttons they can't use.
 * The API enforces the same rules; this is only for a cleaner screen.
 */
export function permissionsFor(roles: string[]): Permissions {
  const has = (...r: string[]) => roles.some(x => r.includes(x))
  return {
    fixExceptions: has('SyncHub.Support', 'SyncHub.Admin'),
    stewardData: has('SyncHub.DataSteward', 'SyncHub.Admin'),
  }
}

/* ---------------- Session expiry ---------------- */

let unauthorizedHandler: () => void = () => {}

/** Called when the API rejects the token (expired, or the directory key changed). */
export function onUnauthorized(handler: () => void) {
  unauthorizedHandler = handler
}

/** Clears the session and tells the app to show the sign-in screen. Called when the API answers 401. */
export function notifyUnauthorized() {
  saveSession(null)
  unauthorizedHandler()
}
