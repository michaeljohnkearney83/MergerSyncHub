import type { ReactNode } from 'react'

type Tone = 'good' | 'warn' | 'bad' | 'info' | 'muted'

const toneFor: Record<string, Tone> = {
  Enabled: 'good',
  Disabled: 'muted',
  Locked: 'bad',
  'Password change due': 'warn',
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

/** A coloured badge for an account or audit status. */
export function StatusBadge({ status }: { status: string }) {
  return <span className={`badge badge-${toneFor[status] ?? 'muted'}`}>{labelFor[status] ?? status}</span>
}

/** A titled card with optional action buttons in its header. */
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

/** A red error message; renders nothing when there is no message. */
export function ErrorBanner({ message }: { message: string | null | undefined }) {
  if (!message) return null
  return <div className="error-banner" role="alert">{message}</div>
}

/** Placeholder text for an empty list. */
export function Empty({ children }: { children: ReactNode }) {
  return <p className="empty">{children}</p>
}
