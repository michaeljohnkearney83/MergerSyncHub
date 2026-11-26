import { useState } from 'react'
import { api } from '../api'
import { formatDateTime, usePolling } from '../util'
import { appName } from './Shared'
import { Empty, ErrorBanner, Panel } from './ui'

/** Security groups and the app role each one grants. */
export function GroupsPanel() {
  const { data, error } = usePolling(api.groups, [], 30_000)

  return (
    <Panel title="Security groups">
      <p className="hint">
        Access is given through groups, not to individuals. Each group grants one application role, which is
        included in the user's sign-in token for that application. This is how app role assignment works in Entra ID.
      </p>
      <ErrorBanner message={error} />
      <div className="group-cards">
        {data?.map(g => (
          <div key={g.groupId} className="group-card">
            <strong>{g.name}</strong>
            <span><span className="app-tag">{g.appRole}</span> <span className="muted small">{appName(g.appRole)}</span></span>
            <span className="muted small">{g.description}</span>
            <span className="small">{g.members} active member{g.members === 1 ? '' : 's'}</span>
          </div>
        ))}
      </div>
    </Panel>
  )
}

/** The directory audit log: who changed which account, and when. */
export function AuditPanel() {
  const [upn, setUpn] = useState('')
  const { data, error } = usePolling(() => api.audit(300, upn.trim() || undefined), [upn], 10_000)

  return (
    <Panel
      title="Audit log"
      actions={<input className="search" placeholder="Filter by username" value={upn} onChange={e => setUpn(e.target.value)} />}
    >
      <p className="hint">Every sign-in, failed attempt, lockout, joiner, mover and leaver action. Entries are never edited or deleted.</p>
      <ErrorBanner message={error} />
      {data && data.length === 0 && <Empty>No entries.</Empty>}
      {data && data.length > 0 && (
        <table>
          <thead><tr><th>When</th><th>Who</th><th>Action</th><th>Account</th><th>Detail</th></tr></thead>
          <tbody>
            {data.map(a => (
              <tr key={a.auditId}>
                <td className="muted">{formatDateTime(a.atUtc)}</td>
                <td className="small">{a.actor}</td>
                <td><span className={`action action-${actionTone(a.action)}`}>{a.action}</span></td>
                <td className="small">{a.targetUpn ?? '—'}</td>
                <td className="small muted">{a.detail ?? ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Panel>
  )
}

function actionTone(action: string): string {
  if (['SignInFailed', 'SignInBlocked', 'AccountLocked', 'UserDisabled'].includes(action)) return 'bad'
  if (['UserCreated', 'UserEnabled', 'UserUnlocked'].includes(action)) return 'good'
  return 'neutral'
}
