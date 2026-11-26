import { currentToken, DIRECTORY_URL, notifyUnauthorized } from './auth'
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
    throw new Error(`Cannot reach the directory at ${DIRECTORY_URL}. Is CorpDirectory.Api running?`)
  }

  if (response.status === 401) {
    notifyUnauthorized()
    throw new Error('Your session has ended. Please sign in again.')
  }
  if (response.status === 403) throw new Error('You need the Directory Administrators group to do this.')

  const text = await response.text()
  if (!response.ok) {
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

export const api = {
  options: () => request<DirectoryOptions>('/options'),
  users: (search: string, status: string) => request<UserSummary[]>(`/users${qs({ search, status })}`),
  user: (id: string) => request<UserDetail>(`/users/${id}`),
  createUser: (body: NewHire) => request<WithTemporaryPassword>('/users', send('POST', body)),
  updateProfile: (id: string, profile: Profile) => request<UserDetail>(`/users/${id}`, send('PUT', profile)),
  setGroups: (id: string, groupIds: number[]) => request<UserDetail>(`/users/${id}/groups`, send('PUT', { groupIds })),
  disable: (id: string, reason: string) => request<UserDetail>(`/users/${id}/disable`, send('POST', { reason })),
  enable: (id: string, reason: string) => request<UserDetail>(`/users/${id}/enable`, send('POST', { reason })),
  resetPassword: (id: string) => request<WithTemporaryPassword>(`/users/${id}/reset-password`, send('POST')),
  unlock: (id: string) => request<UserDetail>(`/users/${id}/unlock`, send('POST')),
  groups: () => request<Group[]>('/groups'),
  audit: (top: number, upn?: string) => request<AuditEvent[]>(`/audit${qs({ top, upn })}`),
}
