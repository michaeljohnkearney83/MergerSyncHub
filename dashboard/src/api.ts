import type {
  ClientLookup, DeadLetter, DeadLetterDetail, Duplicate, ErrorTypeCount, ReconciliationRow, Run, RunEntity, Summary, UnmappedCode,
} from './types'
import { currentToken, notifyUnauthorized } from './auth'

const BASE = (import.meta.env.VITE_API_URL as string | undefined) ?? 'http://localhost:5080'

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const token = currentToken()
  if (!token) {
    notifyUnauthorized()
    throw new Error('Not signed in.')
  }

  let response: Response
  try {
    response = await fetch(`${BASE}${path}`, {
      ...init,
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${token}`,
        ...(init?.headers ?? {}),
      },
    })
  } catch {
    throw new Error(`Cannot reach the Support API at ${BASE}. Is SyncHub.SupportApi running?`)
  }

  if (response.status === 401) {
    notifyUnauthorized()
    throw new Error('Your session has ended. Please sign in again.')
  }
  if (response.status === 403) {
    throw new Error("Your role doesn't allow this. Ask the service desk to add you to the right group.")
  }

  const text = await response.text()
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`
    try {
      const body = JSON.parse(text) as { error?: string; title?: string }
      message = body.error ?? body.title ?? message
    } catch {
      // body was not JSON; keep the status text
    }
    throw new Error(message)
  }
  return (text ? JSON.parse(text) : undefined) as T
}

const qs = (params: Record<string, string | number | boolean | undefined>) => {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== '') search.set(key, String(value))
  }
  const s = search.toString()
  return s ? `?${s}` : ''
}

const post = (body: unknown): RequestInit => ({ method: 'POST', body: JSON.stringify(body) })

export const api = {
  summary: () => request<Summary>('/api/summary'),

  runs: (hideQuiet: boolean) => request<Run[]>(`/api/runs${qs({ top: 100, hideQuiet })}`),
  runEntities: (id: number) => request<RunEntity[]>(`/api/runs/${id}/entities`),

  deadLetters: (status: string, source?: string, errorType?: string) =>
    request<DeadLetter[]>(`/api/deadletters${qs({ status, source, errorType })}`),
  deadLetterSummary: () => request<ErrorTypeCount[]>('/api/deadletters/summary'),
  deadLetter: (id: number) => request<DeadLetterDetail>(`/api/deadletters/${id}`),
  savePayload: (id: number, payload: string, user: string, note?: string) =>
    request<void>(`/api/deadletters/${id}/payload`, { method: 'PUT', body: JSON.stringify({ payload, user, note }) }),
  replay: (id: number, user: string) => request<void>(`/api/deadletters/${id}/replay`, post({ user })),
  replayBulk: (user: string, source?: string, errorType?: string) =>
    request<{ queued: number }>('/api/deadletters/replay-bulk', post({ user, source, errorType })),
  ignore: (id: number, user: string, note: string) =>
    request<void>(`/api/deadletters/${id}/ignore`, post({ user, note })),

  duplicates: (status: string) => request<Duplicate[]>(`/api/duplicates${qs({ status })}`),
  decide: (id: number, decision: Duplicate['status'], user: string) =>
    request<void>(`/api/duplicates/${id}/decision`, post({ decision, user })),

  searchClients: (source: string, search: string) =>
    request<ClientLookup[]>(`/api/clients${qs({ source, search })}`),

  reconciliation: () => request<ReconciliationRow[]>('/api/reconciliation'),

  practiceAreas: () => request<string[]>('/api/practice-areas'),
  unmapped: () => request<UnmappedCode[]>('/api/practice-areas/unmapped'),
  mapPracticeArea: (source: string, sourceCode: string, practiceArea: string) =>
    request<void>('/api/practice-areas/map', post({ source, sourceCode, practiceArea })),
}
