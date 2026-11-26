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

export function formatDateTime(value: string | null | undefined): string {
  if (!value) return '—'
  return parseUtc(value).toLocaleString('en-GB', {
    day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit', second: '2-digit',
  })
}

export function timeAgo(value: string | null | undefined): string {
  if (!value) return 'never'
  const seconds = Math.round((Date.now() - parseUtc(value).getTime()) / 1000)
  if (seconds < 60) return `${Math.max(seconds, 0)}s ago`
  if (seconds < 3600) return `${Math.round(seconds / 60)}m ago`
  if (seconds < 86_400) return `${Math.round(seconds / 3600)}h ago`
  return `${Math.round(seconds / 86_400)}d ago`
}
