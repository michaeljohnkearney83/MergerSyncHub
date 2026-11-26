import { useCallback, useEffect, useRef, useState } from 'react'

/**
 * Loads data and refreshes it on an interval. `deps` reloads immediately when filters change.
 * Keeps showing the last good data if a refresh fails, with the error alongside.
 */
export function usePolling<T>(load: () => Promise<T>, deps: unknown[], intervalMs = 10_000) {
  const [data, setData] = useState<T | undefined>(undefined)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const loadRef = useRef(load)
  loadRef.current = load

  const reload = useCallback(async () => {
    try {
      const result = await loadRef.current()
      setData(result)
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    setLoading(true)
    void reload()
    const timer = window.setInterval(() => void reload(), intervalMs)
    return () => window.clearInterval(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reload, intervalMs, ...deps])

  return { data, error, loading, reload }
}

/** SQL Server datetime2 comes back without a time zone. The API stores UTC, so say so before parsing. */
export function parseUtc(value: string): Date {
  return new Date(/Z$|[+-]\d\d:\d\d$/.test(value) ? value : `${value}Z`)
}

/**
 * A UTC date from the API as UK local date and time, e.g. "26 Nov, 13:58:40". Shows "—" when empty.
 * @param value An API timestamp (UTC, usually without a Z).
 */
export function formatDateTime(value: string | null | undefined): string {
  if (!value) return '—'
  return parseUtc(value).toLocaleString('en-GB', {
    day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit', second: '2-digit',
  })
}

/** How long ago a UTC timestamp was, e.g. "46s ago", "3h ago"; "never" when empty. */
export function timeAgo(value: string | null | undefined): string {
  if (!value) return 'never'
  const seconds = Math.round((Date.now() - parseUtc(value).getTime()) / 1000)
  if (seconds < 60) return `${Math.max(seconds, 0)}s ago`
  if (seconds < 3600) return `${Math.round(seconds / 60)}m ago`
  if (seconds < 86_400) return `${Math.round(seconds / 3600)}h ago`
  return `${Math.round(seconds / 86_400)}d ago`
}

/** A number with thousands separators, e.g. 75,436. */
export const number = (n: number) => n.toLocaleString('en-GB')

/** Splits PascalCase codes for display: CloseDateBeforeOpenDate -> Close date before open date */
export function humanise(code: string): string {
  const spaced = code.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase()
  return spaced.charAt(0).toUpperCase() + spaced.slice(1)
}

/** Pretty-prints JSON if it parses, otherwise returns it unchanged. */
export function prettyJson(text: string): string {
  try {
    return JSON.stringify(JSON.parse(text), null, 2)
  } catch {
    return text
  }
}

const USER_KEY = 'synchub.user'

/** Remembers who is using the dashboard. Storage can be unavailable, so failures are ignored. */
export function loadUser(): string {
  try {
    return window.localStorage.getItem(USER_KEY) ?? ''
  } catch {
    return ''
  }
}

/** Remembers the name typed on this computer; storage failures are ignored. */
export function saveUser(user: string) {
  try {
    window.localStorage.setItem(USER_KEY, user)
  } catch {
    // not fatal: the name just won't be remembered
  }
}
