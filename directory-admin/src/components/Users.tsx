import { useEffect, useState } from 'react'
import { api } from '../api'
import type { AuditEvent, DirectoryOptions, Group, Profile, UserDetail, WithTemporaryPassword } from '../types'
import { formatDateTime, timeAgo, usePolling } from '../util'
import { AccountStatus, GroupPicker, TemporaryPassword } from './Shared'
import { Empty, ErrorBanner, Panel } from './ui'

const STATUSES = [
  { id: 'all', label: 'All' },
  { id: 'enabled', label: 'Enabled' },
  { id: 'disabled', label: 'Disabled' },
] as const

/** Searchable list of users, with the selected user's details and admin actions alongside. */
export function UsersPanel({ selected, onSelect, myUserId }: {
  selected: string | null
  onSelect: (id: string | null) => void
  myUserId: string
}) {
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState<(typeof STATUSES)[number]['id']>('all')
  const list = usePolling(() => api.users(search.trim(), status), [search, status], 15_000)

  return (
    <div className="split">
      <div className="split-main">
        <Panel title="Users">
          <div className="filters">
            <input className="search" placeholder="Search name, username, job title, department or office"
                   value={search} onChange={e => setSearch(e.target.value)} />
            <div className="segmented">
              {STATUSES.map(s => (
                <button key={s.id} className={s.id === status ? 'active' : ''} onClick={() => setStatus(s.id)}>{s.label}</button>
              ))}
            </div>
          </div>
          <ErrorBanner message={list.error} />
          {list.data && list.data.length === 0 && <Empty>No users match.</Empty>}
          {list.data && list.data.length > 0 && (
            <table>
              <thead>
                <tr><th>Name</th><th>Job title</th><th>Department</th><th>Office</th><th>Groups</th><th>Status</th><th>Last sign-in</th></tr>
              </thead>
              <tbody>
                {list.data.map(u => (
                  <tr key={u.userId} className={`clickable ${selected === u.userId ? 'selected' : ''}`} onClick={() => onSelect(u.userId)}>
                    <td>
                      <div><strong>{u.displayName}</strong></div>
                      <div className="muted small">{u.userPrincipalName}</div>
                    </td>
                    <td>{u.jobTitle ?? '—'}</td>
                    <td>{u.department ?? '—'}</td>
                    <td>{u.office ?? '—'}</td>
                    <td className="small">{u.groups ?? <span className="muted">No groups</span>}</td>
                    <td><AccountStatus user={u} /></td>
                    <td className="muted">{timeAgo(u.lastSignInUtc)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </Panel>
      </div>

      {selected && (
        <UserDrawer id={selected} isMe={selected === myUserId} onClose={() => onSelect(null)} onChanged={() => void list.reload()} />
      )}
    </div>
  )
}

function UserDrawer({ id, isMe, onClose, onChanged }: {
  id: string
  isMe: boolean
  onClose: () => void
  onChanged: () => void
}) {
  const [user, setUser] = useState<UserDetail | null>(null)
  const [profile, setProfile] = useState<Profile | null>(null)
  const [groupIds, setGroupIds] = useState<number[]>([])
  const [reason, setReason] = useState('')
  const [secret, setSecret] = useState<WithTemporaryPassword | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [activity, setActivity] = useState<AuditEvent[]>([])

  const options = usePolling<DirectoryOptions>(api.options, [], 600_000)
  const groups = usePolling<Group[]>(api.groups, [], 600_000)

  function show(u: UserDetail) {
    setUser(u)
    setProfile({
      givenName: u.givenName, surname: u.surname, jobTitle: u.jobTitle ?? '', department: u.department ?? '',
      office: u.office ?? '', employeeId: u.employeeId ?? '',
    })
    setGroupIds(u.groupIds)
    void api.audit(25, u.userPrincipalName).then(setActivity).catch(() => setActivity([]))
  }

  useEffect(() => {
    setUser(null)
    setSecret(null)
    setMessage(null)
    setError(null)
    setReason('')
    api.user(id).then(show).catch(e => setError(e instanceof Error ? e.message : String(e)))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id])

  async function act(action: () => Promise<UserDetail | WithTemporaryPassword>, success: string) {
    setBusy(true)
    setError(null)
    setMessage(null)
    try {
      const result = await action()
      if ('temporaryPassword' in result) {
        setSecret(result)
        show(result.user)
      } else {
        show(result)
      }
      setMessage(success)
      onChanged()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const set = (key: keyof Profile) => (e: { target: { value: string } }) =>
    setProfile(p => (p ? { ...p, [key]: e.target.value } : p))

  const groupsChanged = user !== null &&
    (groupIds.length !== user.groupIds.length || groupIds.some(g => !user.groupIds.includes(g)))

  const profileChanged = user !== null && profile !== null && (
    profile.givenName !== user.givenName || profile.surname !== user.surname ||
    profile.jobTitle !== (user.jobTitle ?? '') || profile.department !== (user.department ?? '') ||
    profile.office !== (user.office ?? '') || profile.employeeId !== (user.employeeId ?? ''))

  return (
    <aside className="drawer">
      <header className="drawer-header">
        <h2>{user?.displayName ?? 'User'}{isMe && <span className="muted small"> (you)</span>}</h2>
        <button className="link" onClick={onClose} aria-label="Close">✕</button>
      </header>

      <ErrorBanner message={error} />
      {message && <div className="notice">{message}</div>}
      {!user && !error && <p className="muted">Loading…</p>}

      {user && profile && (
        <>
          <dl className="facts">
            <dt>Username</dt><dd><code>{user.userPrincipalName}</code></dd>
            <dt>Status</dt><dd><AccountStatus user={user} /></dd>
            <dt>Created</dt><dd>{formatDateTime(user.createdAtUtc)} by {user.createdBy}</dd>
            <dt>Last sign-in</dt><dd>{user.lastSignInUtc ? formatDateTime(user.lastSignInUtc) : 'Never'}</dd>
            <dt>Password set</dt><dd>{user.passwordChangedUtc ? formatDateTime(user.passwordChangedUtc) : 'Not yet (temporary or seed password)'}</dd>
            {user.failedSignInCount > 0 && <><dt>Failed attempts</dt><dd>{user.failedSignInCount}</dd></>}
          </dl>

          {secret && (
            <TemporaryPassword upn={secret.user.userPrincipalName} password={secret.temporaryPassword}
                               intro="Password reset. Give the user these details:" />
          )}

          <div className="section-title">Profile</div>
          <div className="form-grid">
            <label>First name<input value={profile.givenName} onChange={set('givenName')} /></label>
            <label>Surname<input value={profile.surname} onChange={set('surname')} /></label>
            <label className="full">Job title<input value={profile.jobTitle} onChange={set('jobTitle')} /></label>
            <label>Department
              <select value={profile.department} onChange={set('department')}>
                <option value="">—</option>
                {withCurrent(options.data?.departments, profile.department).map(d => <option key={d} value={d}>{d}</option>)}
              </select>
            </label>
            <label>Office
              <select value={profile.office} onChange={set('office')}>
                <option value="">—</option>
                {withCurrent(options.data?.offices, profile.office).map(o => <option key={o} value={o}>{o}</option>)}
              </select>
            </label>
            <label>Employee id<input value={profile.employeeId} onChange={set('employeeId')} /></label>
          </div>
          <div className="button-row">
            <button className="primary" disabled={busy || !profileChanged}
                    onClick={() => void act(() => api.updateProfile(id, profile), 'Profile saved.')}>Save profile</button>
          </div>

          <div className="section-title">Access</div>
          {groups.data && <GroupPicker groups={groups.data} selected={groupIds} onChange={setGroupIds} disabled={busy} />}
          <div className="button-row">
            <button className="primary" disabled={busy || !groupsChanged}
                    onClick={() => void act(() => api.setGroups(id, groupIds), 'Access updated. It applies the next time they sign in.')}>
              Save access
            </button>
            {groupsChanged && <button className="link" onClick={() => setGroupIds(user.groupIds)}>Undo changes</button>}
          </div>

          <div className="section-title">Account</div>
          <div className="button-row">
            <button disabled={busy}
                    onClick={() => void act(() => api.resetPassword(id), 'Temporary password issued.')}>Reset password</button>
            {user.isLocked && (
              <button disabled={busy} onClick={() => void act(() => api.unlock(id), 'Account unlocked.')}>Unlock</button>
            )}
          </div>
          <label className="field-label" htmlFor="reason">Reason (recorded in the audit log)</label>
          <input id="reason" value={reason} onChange={e => setReason(e.target.value)}
                 placeholder={user.accountEnabled ? 'e.g. Leaver: last day 28 Nov' : 'e.g. Returned from leave'} style={{ width: '100%' }} />
          <div className="button-row">
            {user.accountEnabled ? (
              <button className="danger" disabled={busy || isMe || !reason.trim()}
                      onClick={() => {
                        if (window.confirm(`Disable ${user.displayName}? They will no longer be able to sign in.`))
                          void act(() => api.disable(id, reason.trim()), 'Account disabled.')
                      }}>
                Disable account
              </button>
            ) : (
              <button disabled={busy || !reason.trim()}
                      onClick={() => void act(() => api.enable(id, reason.trim()), 'Account enabled.')}>Enable account</button>
            )}
            {isMe && <span className="muted small">You can't disable your own account.</span>}
          </div>
          <p className="hint">
            Disabling stops new sign-ins straight away. A token they already hold stays valid until it expires
            (60 minutes); real directories shorten that with token revocation and continuous access evaluation.
          </p>

          <div className="section-title">Recent activity</div>
          {activity.length === 0 && <p className="muted small">No activity yet.</p>}
          {activity.map(a => (
            <div key={a.auditId} className="small activity">
              <strong>{a.action}</strong> · {formatDateTime(a.atUtc)} · {a.actor}
              {a.detail && <div className="muted">{a.detail}</div>}
            </div>
          ))}
        </>
      )}
    </aside>
  )
}

/** Keeps a value that isn't in the configured list (e.g. an old office) selectable. */
function withCurrent(list: string[] | undefined, current: string): string[] {
  const values = list ?? []
  return current && !values.includes(current) ? [...values, current] : values
}
