import { useEffect, useState } from 'react'
import { loadSession, onUnauthorized, saveSession, type Session } from './auth'
import { AuditPanel, GroupsPanel } from './components/GroupsAndAudit'
import { NewStarterPanel } from './components/NewStarter'
import { SignIn } from './components/SignIn'
import { UsersPanel } from './components/Users'

type Tab = 'users' | 'new' | 'groups' | 'audit'

const TABS: { id: Tab; label: string }[] = [
  { id: 'users', label: 'Users' },
  { id: 'new', label: 'New starter' },
  { id: 'groups', label: 'Groups' },
  { id: 'audit', label: 'Audit log' },
]

export default function App() {
  const [tab, setTab] = useState<Tab>('users')
  const [session, setSession] = useState<Session | null>(loadSession)
  const [notice, setNotice] = useState<string | null>(null)
  const [selectedUser, setSelectedUser] = useState<string | null>(null)

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
  }

  if (!session) {
    return <SignIn notice={notice} onSignedIn={s => { setSession(s); setNotice(null) }} />
  }

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <span className="brand-mark">◈</span>
          <div>
            <div className="brand-name">Directory admin centre</div>
            <div className="brand-sub">Winston Taylor · users and access</div>
          </div>
        </div>
        <nav className="tabs">
          {TABS.map(t => (
            <button key={t.id} className={t.id === tab ? 'active' : ''} onClick={() => setTab(t.id)}>{t.label}</button>
          ))}
        </nav>
        <div className="user">
          <span className="avatar" aria-hidden>{initials(session.user.displayName)}</span>
          <div className="user-text">
            <strong>{session.user.displayName}</strong>
            <span className="user-roles">Directory administrator</span>
          </div>
          <button className="signout" onClick={signOut}>Sign out</button>
        </div>
      </header>

      <main className="content">
        {tab === 'users' && (
          <UsersPanel selected={selectedUser} onSelect={setSelectedUser} myUserId={session.user.userId} />
        )}
        {tab === 'new' && (
          <NewStarterPanel onCreated={id => { setSelectedUser(id); setTab('users') }} />
        )}
        {tab === 'groups' && <GroupsPanel />}
        {tab === 'audit' && <AuditPanel />}
      </main>
    </div>
  )
}

function initials(name: string): string {
  const parts = name.split(/\s+/).filter(Boolean)
  return ((parts[0]?.[0] ?? '') + (parts.length > 1 ? parts[parts.length - 1][0] : '')).toUpperCase()
}
