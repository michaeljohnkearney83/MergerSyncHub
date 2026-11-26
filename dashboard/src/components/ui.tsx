import type { ReactNode } from 'react'

type Tone = 'good' | 'warn' | 'bad' | 'info' | 'muted'

const toneFor: Record<string, Tone> = {
  Succeeded: 'good',
  Reconciled: 'good',
  Resolved: 'good',
  ConfirmedDuplicate: 'info',
  SucceededWithErrors: 'warn',
  ExplainedByExceptions: 'warn',
  New: 'warn',
  Pending: 'warn',
  ReplayRequested: 'info',
  Running: 'info',
  Failed: 'bad',
  Investigate: 'bad',
  Ignored: 'muted',
  NotDuplicate: 'muted',
}

const labelFor: Record<string, string> = {
  SucceededWithErrors: 'Succeeded with errors',
  ExplainedByExceptions: 'Explained by exceptions',
  ReplayRequested: 'Replay queued',
  ConfirmedDuplicate: 'Confirmed duplicate',
  NotDuplicate: 'Not a duplicate',
}

export function StatusBadge({ status }: { status: string }) {
  return <span className={`badge badge-${toneFor[status] ?? 'muted'}`}>{labelFor[status] ?? status}</span>
}

export function SourceTag({ source }: { source: string }) {
  return <span className={`source source-${source.toLowerCase()}`}>{source === 'FirmA' ? 'Firm A · UK' : source === 'FirmB' ? 'Firm B · US' : source}</span>
}

export function Panel({ title, actions, children }: { title: string; actions?: ReactNode; children: ReactNode }) {
  return (
    <section className="panel">
      <header className="panel-header">
        <h2>{title}</h2>
        {actions && <div className="panel-actions">{actions}</div>}
      </header>
      {children}
    </section>
  )
}

export function ErrorBanner({ message }: { message: string | null | undefined }) {
  if (!message) return null
  return <div className="error-banner" role="alert">{message}</div>
}

export function Empty({ children }: { children: ReactNode }) {
  return <p className="empty">{children}</p>
}
