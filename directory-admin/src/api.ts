import { currentToken, DIRECTORY_URL, notifyUnauthorized } from './auth'
import { reportApiError, reportApiUnreachable } from './telemetry'
import type { AuditEvent, DirectoryOptions, Group, Profile, UserDetail, UserSummary, WithTemporaryPassword } from './types'

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const token = currentToken()
  if (!token) {
    notifyUnauthorized()
    throw new Error('Not signed in.')
  }

  let response: Response
  try {
    response = await fetch(`${DIRECTORY_URL}/api/admin${path}`, {
      ...init,
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}`, ...(init?.headers ?? {}) },
    })
  } catch {
    reportApiUnreachable('directory-api', `/api/admin${path}`)
    throw new Error(`Cannot reach the directory at ${DIRECTORY_URL}. Is CorpDirectory.Api running?`)
  }

  if (response.status === 401) {
    notifyUnauthorized()
    throw new Error('Your session has ended. Please sign in again.')
  }
  if (response.status === 403) throw new Error('You need the Directory Administrators group to do this.')

  const text = await response.text()
  if (!response.ok) {
    reportApiError('directory-api', `/api/admin${path}`, response.status)
    let message = `${response.status} ${response.statusText}`
    try {
      const body = JSON.parse(text) as { message?: string; error?: string; title?: string }
      message = body.message ?? body.error ?? body.title ?? message
    } catch {
      // not JSON
    }
    throw new Error(message)
  }
  return (text ? JSON.parse(text) : undefined) as T
}

const send = (method: string, body?: unknown): RequestInit =>
  ({ method, body: body === undefined ? undefined : JSON.stringify(body) })

const qs = (params: Record<string, string | number | undefined>) => {
  const search = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== '') search.set(k, String(v))
  const s = search.toString()
  return s ? `?${s}` : ''
}

export interface NewHire extends Profile {
  userPrincipalName: string
  groupIds: number[]
}

/**
 * Typed client for the directory's admin API. Every call sends the signed-in admin's token and needs the
 * Directory.Admin role; failures throw an Error with a message fit to show the user.
 */
export const api = {
  /** Offices, departments and the email domain, for the new-starter form. */
  options: () => request<DirectoryOptions>('/options'),
  /**
   * Users matching the search.
   * @param status 'all', 'enabled', 'disabled' or 'locked'.
   */
  users: (search: string, status: string) => request<UserSummary[]>(`/users${qs({ search, status })}`),
  /** One user's profile, status and groups. */
  user: (id: string) => request<UserDetail>(`/users/${id}`),
  /**
   * Creates a new starter.
   * @returns The new user, with a one-time temporary password to pass on securely.
   */
  createUser: (body: NewHire) => request<WithTemporaryPassword>('/users', send('POST', body)),
  /** Updates job title, department and office. */
  updateProfile: (id: string, profile: Profile) => request<UserDetail>(`/users/${id}`, send('PUT', profile)),
  /** Replaces the user's group memberships, which decide their app roles. */
  setGroups: (id: string, groupIds: number[]) => request<UserDetail>(`/users/${id}/groups`, send('PUT', { groupIds })),
  /** Disables an account (a leaver). Existing tokens still work until they expire. */
  disable: (id: string, reason: string) => request<UserDetail>(`/users/${id}/disable`, send('POST', { reason })),
  /** Re-enables a disabled account. */
  enable: (id: string, reason: string) => request<UserDetail>(`/users/${id}/enable`, send('POST', { reason })),
  /** Issues a temporary password the user must change at next sign-in. */
  resetPassword: (id: string) => request<WithTemporaryPassword>(`/users/${id}/reset-password`, send('POST')),
  /** Clears a lockout after too many wrong passwords. */
  unlock: (id: string) => request<UserDetail>(`/users/${id}/unlock`, send('POST')),
  /** Security groups, the app role each grants, and member counts. */
  groups: () => request<Group[]>('/groups'),
  /** Recent audit events, optionally for one user. */
  audit: (top: number, upn?: string) => request<AuditEvent[]>(`/audit${qs({ top, upn })}`),
}
