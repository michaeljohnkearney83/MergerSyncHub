import { useState } from 'react'
import { api } from '../api'
import type { DirectoryOptions, Group, WithTemporaryPassword } from '../types'
import { usePolling } from '../util'
import { GroupPicker, TemporaryPassword } from './Shared'
import { ErrorBanner, Panel } from './ui'

const EMPTY = { givenName: '', surname: '', jobTitle: '', department: '', office: '', employeeId: '', userPrincipalName: '' }

/** Joiner process: create the account, give access, hand over a one-time password. */
export function NewStarterPanel({ onCreated }: { onCreated: (userId: string) => void }) {
  const [form, setForm] = useState(EMPTY)
  const [groupIds, setGroupIds] = useState<number[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [created, setCreated] = useState<WithTemporaryPassword | null>(null)

  const options = usePolling<DirectoryOptions>(api.options, [], 600_000)
  const groups = usePolling<Group[]>(api.groups, [], 600_000)

  const set = (key: keyof typeof EMPTY) => (e: { target: { value: string } }) =>
    setForm(f => ({ ...f, [key]: e.target.value }))

  const domain = options.data?.emailDomain ?? 'winstontaylor.example'
  const slug = (s: string) => s.normalize('NFD').replace(/[^A-Za-z]/g, '').toLowerCase()
  const suggestedUpn = form.givenName.trim() && form.surname.trim()
    ? `${slug(form.givenName)}.${slug(form.surname)}@${domain}`
    : ''
  const valid = form.givenName.trim() !== '' && form.surname.trim() !== ''

  async function submit(e: { preventDefault: () => void }) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      setCreated(await api.createUser({ ...form, groupIds }))
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  function startAgain() {
    setForm(EMPTY)
    setGroupIds([])
    setCreated(null)
  }

  if (created) {
    return (
      <Panel title="New starter created">
        <TemporaryPassword
          upn={created.user.userPrincipalName}
          password={created.temporaryPassword}
          intro={`${created.user.displayName}'s account is ready. Give them these details securely (not by email to a shared inbox).`} />
        <div className="button-row">
          <button className="primary" onClick={startAgain}>Add another starter</button>
          <button onClick={() => onCreated(created.user.userId)}>Open their account</button>
        </div>
      </Panel>
    )
  }

  return (
    <Panel title="Add a new starter">
      <p className="hint">
        Creates the account with a one-time password and the access you choose. The new starter must set their own
        password the first time they sign in.
      </p>
      <ErrorBanner message={error ?? options.error ?? groups.error} />

      <form onSubmit={submit}>
        <div className="section-title">Person</div>
        <div className="form-grid">
          <label>First name *<input value={form.givenName} onChange={set('givenName')} autoFocus /></label>
          <label>Surname *<input value={form.surname} onChange={set('surname')} /></label>
          <label>Job title<input value={form.jobTitle} onChange={set('jobTitle')} placeholder="e.g. Trainee Solicitor" /></label>
          <label>Employee id<input value={form.employeeId} onChange={set('employeeId')} placeholder="from HR" /></label>
          <label>Department
            <select value={form.department} onChange={set('department')}>
              <option value="">Choose…</option>
              {options.data?.departments.map(d => <option key={d} value={d}>{d}</option>)}
            </select>
          </label>
          <label>Office
            <select value={form.office} onChange={set('office')}>
              <option value="">Choose…</option>
              {options.data?.offices.map(o => <option key={o} value={o}>{o}</option>)}
            </select>
          </label>
          <label className="full">Username (optional)
            <input value={form.userPrincipalName} onChange={set('userPrincipalName')}
                   placeholder={suggestedUpn || `first.last@${domain}`} />
            <span className="muted small" style={{ fontWeight: 'normal' }}>
              Leave blank to use {suggestedUpn || `first.last@${domain}`}. A number is added if it's already taken.
            </span>
          </label>
        </div>

        <div className="section-title">Access</div>
        {groups.data && <GroupPicker groups={groups.data} selected={groupIds} onChange={setGroupIds} />}
        <p className="hint">Give only the access the role needs. More can be added later.</p>

        <div className="button-row">
          <button className="primary" type="submit" disabled={!valid || busy}>{busy ? 'Creating…' : 'Create account'}</button>
        </div>
      </form>
    </Panel>
  )
}
