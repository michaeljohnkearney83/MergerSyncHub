import { useEffect, useState } from 'react'
import { loadSession, onUnauthorized, permissionsFor, ROLE_LABELS, saveSession, type Session } from './auth'
import { DuplicatesPanel } from './components/Duplicates'
import { ExceptionsPanel } from './components/Exceptions'
import { MappingsPanel } from './components/Mappings'
import { ReconciliationPanel, RunsPanel, SummaryTiles } from './components/Overview'
import { SignIn } from './components/SignIn'

type Tab = 'overview' | 'exceptions' | 'duplicates' | 'mappings'

const TABS: { id: Tab; label: string }[] = [
  { id: 'overview', label: 'Overview' },
  { id: 'exceptions', label: 'Exceptions' },
  { id: 'duplicates', label: 'Duplicate clients' },
  { id: 'mappings', label: 'Mappings' },
]

export default function App() {
  const [tab, setTab] = useState<Tab>('overview')
  const [session, setSession] = useState<Session | null>(loadSession)
  const [notice, setNotice] = useState<string | null>(null)

  // If the API rejects our token (expired, or the account changed), go back to sign-in
  useEffect(() => {
    onUnauthorized(() => {
      setSession(null)
      setNotice('Your session has ended. Please sign in again.')
    })
  }, [])

  function signOut() {
    saveSession(null)
    setSession(null)
    setNotice(null)
    setTab('overview')
  }

  if (!session) {
    return <SignIn notice={notice} onSignedIn={s => { setSession(s); setNotice(null) }} />
  }

  const { user } = session
  const can = permissionsFor(user.roles)

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <span className="brand-mark">⇄</span>
          <div>
            <div className="brand-name">SyncHub Support</div>
            <div className="brand-sub">Client &amp; matter integration · Firm A + Firm B</div>
          </div>
        </div>
        <nav className="tabs">
          {TABS.map(t => (
            <button key={t.id} className={t.id === tab ? 'active' : ''} onClick={() => setTab(t.id)}>{t.label}</button>
          ))}
        </nav>
        <div className="user">
          <span className="avatar" aria-hidden>{initials(user.displayName)}</span>
          <div className="user-text">
            <strong>{user.displayName}</strong>
            <span className="user-roles">{user.roles.map(r => ROLE_LABELS[r] ?? r).join(' · ')}</span>
          </div>
          <button className="signout" onClick={signOut}>Sign out</button>
        </div>
      </header>

      <main className="content">
        {tab === 'overview' && (
          <>
            <SummaryTiles onNavigate={setTab} />
            <ReconciliationPanel />
            <RunsPanel />
          </>
        )}
        {tab === 'exceptions' && <ExceptionsPanel user={user.displayName} canFix={can.fixExceptions} />}
        {tab === 'duplicates' && <DuplicatesPanel user={user.displayName} canDecide={can.stewardData} />}
        {tab === 'mappings' && <MappingsPanel canEdit={can.stewardData} />}
      </main>
    </div>
  )
}

function initials(name: string): string {
  const parts = name.split(/\s+/).filter(Boolean)
  return ((parts[0]?.[0] ?? '') + (parts.length > 1 ? parts[parts.length - 1][0] : '')).toUpperCase()
}
