import { useState } from 'react'
import { AuthError, changePassword, signIn, type Session } from '../auth'

type Step = 'credentials' | 'change'

/**
 * Sign-in against the corporate directory. Handles the first-sign-in password change
 * that new starters (and anyone given a reset) must complete.
 */
export function SignIn({ onSignedIn, notice }: { onSignedIn: (s: Session) => void; notice?: string | null }) {
  const [step, setStep] = useState<Step>('credentials')
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submitCredentials(e: { preventDefault: () => void }) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      onSignedIn(await signIn(username.trim(), password))
    } catch (err) {
      if (err instanceof AuthError && err.code === 'password_change_required') {
        setStep('change')
      } else {
        setError(err instanceof Error ? err.message : String(err))
      }
    } finally {
      setBusy(false)
    }
  }

  async function submitChange(e: { preventDefault: () => void }) {
    e.preventDefault()
    if (newPassword !== confirm) {
      setError("The new passwords don't match.")
      return
    }
    setBusy(true)
    setError(null)
    try {
      await changePassword(username.trim(), password, newPassword)
      onSignedIn(await signIn(username.trim(), newPassword))
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="signin-page">
      <div className="signin-card">
        <div className="brand signin-brand">
          <span className="brand-mark">◈</span>
          <div>
            <div className="brand-name">Directory admin centre</div>
            <div className="brand-sub signin-sub">Winston Taylor · users and access</div>
          </div>
        </div>

        {step === 'credentials' ? (
          <form onSubmit={submitCredentials} className="signin-form">
            <h1>Sign in</h1>
            <p className="muted">For service desk and IT administrators.</p>
            {notice && <div className="notice">{notice}</div>}
            {error && <div className="error-banner" role="alert">{error}</div>}

            <label className="field-label" htmlFor="username">Username</label>
            <input id="username" autoFocus autoComplete="username" value={username}
                   onChange={e => setUsername(e.target.value)} placeholder="first.last@winstontaylor.example" />

            <label className="field-label" htmlFor="password">Password</label>
            <input id="password" type="password" autoComplete="current-password" value={password}
                   onChange={e => setPassword(e.target.value)} />

            <button className="primary signin-button" type="submit"
                    disabled={busy || !username.trim() || !password}>
              {busy ? 'Signing in…' : 'Sign in'}
            </button>
            <p className="hint signin-note">
              Mock directory standing in for Active Directory / Entra ID. Seed admin: it.admin (password in the README).
            </p>
          </form>
        ) : (
          <form onSubmit={submitChange} className="signin-form">
            <h1>Choose a new password</h1>
            <p className="muted">
              You're signing in with a temporary password. Choose your own to continue.
            </p>
            {error && <div className="error-banner" role="alert">{error}</div>}

            <label className="field-label" htmlFor="new-password">New password</label>
            <input id="new-password" type="password" autoFocus autoComplete="new-password" value={newPassword}
                   onChange={e => setNewPassword(e.target.value)} />
            <label className="field-label" htmlFor="confirm-password">Confirm new password</label>
            <input id="confirm-password" type="password" autoComplete="new-password" value={confirm}
                   onChange={e => setConfirm(e.target.value)} />
            <p className="hint">At least 10 characters, with a capital letter, a lower-case letter and a number.</p>

            <button className="primary signin-button" type="submit" disabled={busy || !newPassword || !confirm}>
              {busy ? 'Saving…' : 'Set password and sign in'}
            </button>
            <button type="button" className="link" onClick={() => { setStep('credentials'); setError(null) }}>
              Back
            </button>
          </form>
        )}
      </div>
    </div>
  )
}
