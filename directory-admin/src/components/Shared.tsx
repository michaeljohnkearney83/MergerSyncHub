import { useState } from 'react'
import type { Group, UserDetail, UserSummary } from '../types'
import { StatusBadge } from './ui'

/** Account status badges: Enabled/Disabled, plus Locked and Password change due where relevant. */
export function AccountStatus({ user }: { user: Pick<UserSummary | UserDetail, 'accountEnabled' | 'isLocked' | 'mustChangePassword'> }) {
  return (
    <span className="status-stack">
      <StatusBadge status={user.accountEnabled ? 'Enabled' : 'Disabled'} />
      {user.isLocked && <StatusBadge status="Locked" />}
      {user.accountEnabled && user.mustChangePassword && <StatusBadge status="Password change due" />}
    </span>
  )
}

/** Shows a one-time password once, with a copy button. It is never stored or shown again. */
export function TemporaryPassword({ upn, password, intro }: { upn: string; password: string; intro: string }) {
  const [copied, setCopied] = useState(false)

  async function copy() {
    try {
      await navigator.clipboard.writeText(`Username: ${upn}\nTemporary password: ${password}`)
      setCopied(true)
    } catch {
      setCopied(false)
    }
  }

  return (
    <div className="secret">
      <div>{intro}</div>
      <div><span className="muted small">Username</span><br /><code>{upn}</code></div>
      <div><span className="muted small">Temporary password</span><br /><span className="secret-value">{password}</span></div>
      <div className="button-row">
        <button onClick={() => void copy()}>{copied ? 'Copied ✓' : 'Copy details'}</button>
        <span className="muted small">This is shown once. They'll choose their own password at first sign-in.</span>
      </div>
    </div>
  )
}

/** Checkbox list of security groups, grouped by the application they give access to. */
export function GroupPicker({ groups, selected, onChange, disabled }: {
  groups: Group[]
  selected: number[]
  onChange: (ids: number[]) => void
  disabled?: boolean
}) {
  const toggle = (id: number) =>
    onChange(selected.includes(id) ? selected.filter(g => g !== id) : [...selected, id])

  const apps = Array.from(new Set(groups.map(g => appName(g.appRole))))

  return (
    <div className="group-list">
      {apps.map(app => (
        <div key={app} className="group-list">
          <div className="muted small">{app}</div>
          {groups.filter(g => appName(g.appRole) === app).map(g => (
            <label key={g.groupId} className="group-option">
              <input type="checkbox" disabled={disabled} checked={selected.includes(g.groupId)} onChange={() => toggle(g.groupId)} />
              <span>
                <span className="group-name">{g.name}</span> <span className="app-tag">{g.appRole}</span>
                <br /><span className="muted small">{g.description}</span>
              </span>
            </label>
          ))}
        </div>
      ))}
    </div>
  )
}

/** The application an app role belongs to, by its prefix, e.g. "SyncHub.Support" is the SyncHub Support dashboard. */
export function appName(appRole: string): string {
  if (appRole.startsWith('SyncHub.')) return 'SyncHub Support dashboard'
  if (appRole.startsWith('Directory.')) return 'Directory admin centre'
  return 'Other'
}
