import type {
  ClientLookup, DeadLetter, DeadLetterDetail, Duplicate, ErrorTypeCount, ReconciliationRow, Run, RunEntity, Summary, UnmappedCode,
} from './types'
import { currentToken, notifyUnauthorized } from './auth'
import { reportApiError, reportApiUnreachable } from './telemetry'

const BASE = (import.meta.env.VITE_API_URL as string | undefined) ?? 'http://localhost:5081'

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
    reportApiUnreachable('support-api', path)
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
    reportApiError('support-api', path, response.status)
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

/**
 * Typed client for the Support API. Every call sends the signed-in user's token; failures throw an
 * Error with a message fit to show the user. Note: the `user` arguments are ignored by the API (identity
 * comes from the token); they are left over from before sign-in existed.
 */
export const api = {
  /** Headline numbers for the Overview tiles. */
  summary: () => request<Summary>('/api/summary'),

  /**
   * The 100 most recent sync and replay runs.
   * @param hideQuiet Leave out successful runs that changed nothing.
   */
  runs: (hideQuiet: boolean) => request<Run[]>(`/api/runs${qs({ top: 100, hideQuiet })}`),
  /** Per-entity counts for one run. */
  runEntities: (id: number) => request<RunEntity[]>(`/api/runs/${id}/entities`),

  /**
   * Dead letters for the Exceptions page.
   * @param status 'Open', 'All', or an exact status such as 'Ignored'.
   */
  deadLetters: (status: string, source?: string, errorType?: string) =>
    request<DeadLetter[]>(`/api/deadletters${qs({ status, source, errorType })}`),
  /** Open dead letters grouped by source and error type. */
  deadLetterSummary: () => request<ErrorTypeCount[]>('/api/deadletters/summary'),
  /** One dead letter with its full payload. */
  deadLetter: (id: number) => request<DeadLetterDetail>(`/api/deadletters/${id}`),
  /** Saves a corrected payload so the record can be replayed. Support or Admin only. */
  savePayload: (id: number, payload: string, user: string, note?: string) =>
    request<void>(`/api/deadletters/${id}/payload`, { method: 'PUT', body: JSON.stringify({ payload, user, note }) }),
  /** Queues one dead letter for the worker to replay. Support or Admin only. */
  replay: (id: number, user: string) => request<void>(`/api/deadletters/${id}/replay`, post({ user })),
  /**
   * Queues every New dead letter matching the filters. Support or Admin only.
   * @returns How many were queued.
   */
  replayBulk: (user: string, source?: string, errorType?: string) =>
    request<{ queued: number }>('/api/deadletters/replay-bulk', post({ user, source, errorType })),
  /** Closes a dead letter as Ignored; a note explaining why is required. Support or Admin only. */
  ignore: (id: number, user: string, note: string) =>
    request<void>(`/api/deadletters/${id}/ignore`, post({ user, note })),

  /** Suggested duplicate client pairs with the given status (Pending by default on the page). */
  duplicates: (status: string) => request<Duplicate[]>(`/api/duplicates${qs({ status })}`),
  /** Records a decision on a suggested pair. Data Steward or Admin only. */
  decide: (id: number, decision: Duplicate['status'], user: string) =>
    request<void>(`/api/duplicates/${id}/decision`, post({ decision, user })),

  /** Clients in one source matching a name or key, for the correction form's client picker. */
  searchClients: (source: string, search: string) =>
    request<ClientLookup[]>(`/api/clients${qs({ source, search })}`),

  /** Source totals against hub counts, per source and entity. */
  reconciliation: () => request<ReconciliationRow[]>('/api/reconciliation'),

  /** Practice areas already in use, for the mapping drop-down. */
  practiceAreas: () => request<string[]>('/api/practice-areas'),
  /** Practice-area codes with no mapping yet. */
  unmapped: () => request<UnmappedCode[]>('/api/practice-areas/unmapped'),
  /** Maps a source code to a firm-wide practice area. Data Steward or Admin only. */
  mapPracticeArea: (source: string, sourceCode: string, practiceArea: string) =>
    request<void>('/api/practice-areas/map', post({ source, sourceCode, practiceArea })),
}
