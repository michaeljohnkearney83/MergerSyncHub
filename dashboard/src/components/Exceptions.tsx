import { useEffect, useState } from 'react'
import { api } from '../api'
import type { DeadLetterDetail } from '../types'
import { formatDateTime, humanise, number, prettyJson, timeAgo, usePolling } from '../util'
import { parseRecord, RecordForm, schemaFor, validateRecord } from './RecordForm'
import { Empty, ErrorBanner, Panel, SourceTag, StatusBadge } from './ui'

const STATUSES = ['Open', 'Resolved', 'Ignored', 'All'] as const

/** The exception queue: filter dead letters, then fix, replay or ignore them (if the role allows). */
export function ExceptionsPanel({ user, canFix }: { user: string; canFix: boolean }) {
  const [status, setStatus] = useState<(typeof STATUSES)[number]>('Open')
  const [source, setSource] = useState('')
  const [errorType, setErrorType] = useState('')
  const [selected, setSelected] = useState<number | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const list = usePolling(() => api.deadLetters(status, source, errorType), [status, source, errorType], 10_000)
  const breakdown = usePolling(api.deadLetterSummary, [], 10_000)

  async function replayAll() {
    if (!user) return setNotice('Sign in first, so changes are attributed.')
    const label = [source || 'all sources', errorType ? humanise(errorType) : 'all error types'].join(', ')
    if (!window.confirm(`Queue every open exception (${label}) for replay?`)) return
    try {
      const { queued } = await api.replayBulk(user, source || undefined, errorType || undefined)
      setNotice(`${queued} item(s) queued. The worker will replay them on its next cycle.`)
      await list.reload()
    } catch (e) {
      setNotice(e instanceof Error ? e.message : String(e))
    }
  }

  return (
    <div className="split">
      <div className="split-main">
        <Panel
          title="Integration exceptions"
          actions={status === 'Open' && canFix && <button onClick={() => void replayAll()}>Replay all shown</button>}
        >
          <div className="filters">
            <div className="segmented">
              {STATUSES.map(s => (
                <button key={s} className={s === status ? 'active' : ''} onClick={() => setStatus(s)}>{s}</button>
              ))}
            </div>
            <select value={source} onChange={e => setSource(e.target.value)}>
              <option value="">All sources</option>
              <option value="FirmA">Firm A (UK)</option>
              <option value="FirmB">Firm B (US)</option>
            </select>
            {errorType && <button className="link" onClick={() => setErrorType('')}>Clear error filter ✕</button>}
          </div>

          {breakdown.data && breakdown.data.length > 0 && (
            <div className="chips">
              {breakdown.data.map(b => (
                <button key={`${b.source}-${b.errorType}`}
                        className={`chip ${errorType === b.errorType ? 'active' : ''}`}
                        onClick={() => { setErrorType(b.errorType); setSource(b.source) }}>
                  {b.source} · {humanise(b.errorType)} <strong>{number(b.count)}</strong>
                </button>
              ))}
            </div>
          )}

          {notice && <div className="notice" onClick={() => setNotice(null)}>{notice}</div>}
          <ErrorBanner message={list.error} />

          {list.data && list.data.length === 0 && <Empty>Nothing here. 🎉</Empty>}
          {list.data && list.data.length > 0 && (
            <table>
              <thead>
                <tr>
                  <th>Source</th><th>Record</th><th>Problem</th><th>Status</th>
                  <th className="num">Seen</th><th>Last seen</th>
                </tr>
              </thead>
              <tbody>
                {list.data.map(d => (
                  <tr key={d.deadLetterId}
                      className={`clickable ${selected === d.deadLetterId ? 'selected' : ''}`}
                      onClick={() => setSelected(d.deadLetterId)}>
                    <td><SourceTag source={d.source} /></td>
                    <td><span className="muted">{d.entity}</span> <code>{d.sourceKey}</code></td>
                    <td>
                      <div>{humanise(d.errorType)}</div>
                      <div className="muted small truncate">{d.errorMessage}</div>
                    </td>
                    <td><StatusBadge status={d.status} /></td>
                    <td className="num">{d.occurrenceCount}×</td>
                    <td className="muted">{timeAgo(d.lastSeenAtUtc)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </Panel>
      </div>

      {selected !== null && (
        <ExceptionDetail
          id={selected}
          user={user}
          canFix={canFix}
          onClose={() => setSelected(null)}
          onChanged={() => { void list.reload(); void breakdown.reload() }}
        />
      )}
    </div>
  )
}

function ExceptionDetail({ id, user, canFix, onClose, onChanged }: {
  id: number
  user: string
  canFix: boolean
  onClose: () => void
  onChanged: () => void
}) {
  const [item, setItem] = useState<DeadLetterDetail | null>(null)
  const [payload, setPayload] = useState('')
  const [note, setNote] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [rawMode, setRawMode] = useState(false)

  async function load() {
    try {
      const detail = await api.deadLetter(id)
      setItem(detail)
      setPayload(prettyJson(detail.payload))
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  useEffect(() => {
    setItem(null)
    setMessage(null)
    setNote('')
    setRawMode(false)
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id])

  const editable = item?.status === 'New' && canFix
  const payloadChanged = item !== null && payload !== prettyJson(item.payload)

  // Guided form when we know the record's shape; raw JSON for anything else (or on request)
  const schema = item ? schemaFor(item.source, item.entity) : undefined
  const record = parseRecord(payload)
  const formAvailable = schema !== undefined && record !== null && item?.errorType !== 'MalformedPayload'
  const showForm = formAvailable && !rawMode
  const fieldErrors = schema && record ? validateRecord(schema, record) : {}
  const problemCount = Object.keys(fieldErrors).length
  const blockReason = record === null
    ? 'The record is not valid JSON.'
    : problemCount > 0 ? `Fix the ${problemCount === 1 ? 'highlighted problem' : `${problemCount} highlighted problems`} before replaying.` : null

  async function act(action: () => Promise<unknown>, success: string) {
    if (!user) {
      setError('Sign in first, so changes are attributed.')
      return
    }
    setBusy(true)
    setError(null)
    try {
      await action()
      setMessage(success)
      await load()
      onChanged()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const save = () => act(async () => {
    JSON.parse(payload)   // fail fast in the browser if the JSON is broken
    await api.savePayload(id, payload, user, note || undefined)
  }, 'Payload saved. Replay it to push the corrected record through.')

  const replay = () => act(async () => {
    if (payloadChanged) await api.savePayload(id, payload, user, note || undefined)
    await api.replay(id, user)
  }, 'Queued for replay. The worker picks it up on its next cycle (within a minute).')

  const ignore = () => {
    if (!note.trim()) {
      setError('Add a note explaining why this is being ignored.')
      return
    }
    void act(() => api.ignore(id, user, note.trim()), 'Marked as ignored.')
  }

  return (
    <aside className="drawer">
      <header className="drawer-header">
        <h2>Exception #{id}</h2>
        <button className="link" onClick={onClose} aria-label="Close">✕</button>
      </header>

      <ErrorBanner message={error} />
      {message && <div className="notice">{message}</div>}
      {!item && !error && <p className="muted">Loading…</p>}

      {item && (
        <>
          <dl className="facts">
            <dt>Status</dt><dd><StatusBadge status={item.status} /></dd>
            <dt>Record</dt><dd><SourceTag source={item.source} /> {item.entity} <code>{item.sourceKey}</code></dd>
            {item.relatedClientKey && <><dt>Waiting for client</dt><dd><code>{item.relatedClientKey}</code></dd></>}
            <dt>Problem</dt><dd><strong>{humanise(item.errorType)}</strong><br />{item.errorMessage}</dd>
            <dt>Seen</dt><dd>{item.occurrenceCount} time(s), replayed {item.replayAttempts} time(s)</dd>
            <dt>First seen</dt><dd>{formatDateTime(item.createdAtUtc)} (run #{item.firstSyncRunId})</dd>
            <dt>Last seen</dt><dd>{formatDateTime(item.lastSeenAtUtc)} (run #{item.lastSyncRunId})</dd>
            {item.payloadEditedBy && <><dt>Edited</dt><dd>by {item.payloadEditedBy}, {formatDateTime(item.payloadEditedAtUtc)}</dd></>}
            {item.resolutionNote && <><dt>History</dt><dd className="history">{item.resolutionNote.split(' | ').map((h, i) => <div key={i}>{h}</div>)}</dd></>}
          </dl>

          <div className="record-header">
            <span className="field-label">
              {showForm ? 'Correct the record' : 'Source record (raw JSON)'} {!editable && <span className="muted">(read only)</span>}
            </span>
            {formAvailable && (
              <button type="button" className="link small" onClick={() => setRawMode(m => !m)}>
                {rawMode ? 'Back to form' : 'Show raw record'}
              </button>
            )}
          </div>

          {showForm && schema && record ? (
            <RecordForm source={item.source} entity={item.entity} errorType={item.errorType} record={record}
                        errors={fieldErrors} readOnly={!editable}
                        onChange={r => setPayload(JSON.stringify(r, null, 2))} />
          ) : (
            <textarea id="payload" className="payload" spellCheck={false} readOnly={!editable}
                      value={payload} onChange={e => setPayload(e.target.value)} rows={16} />
          )}
          {editable && blockReason && <div className="field-error">{blockReason}</div>}
          {!canFix && item.status === 'New' && (
            <div className="notice">
              You can view exceptions but not change them. Fixing and replaying needs the SyncHub Support Analysts group.
            </div>
          )}
          <p className="hint">
            Fixing the data here unblocks the integration. The lasting fix belongs in the source system,
            otherwise the next change there will bring the bad value back.
          </p>

          {editable && (
            <>
              <label className="field-label" htmlFor="note">Note (optional; required if you ignore the item)</label>
              <input id="note" value={note} onChange={e => setNote(e.target.value)}
                     placeholder="e.g. Confirmed with Accounts: test client, never billed" />
              <div className="button-row">
                <button className="primary" disabled={busy || blockReason !== null} onClick={() => void replay()}>
                  {payloadChanged ? 'Save and replay' : 'Replay'}
                </button>
                <button disabled={busy || !payloadChanged || record === null} onClick={() => void save()}>Save only</button>
                <button className="danger" disabled={busy} onClick={ignore}>Ignore</button>
              </div>
            </>
          )}
        </>
      )}
    </aside>
  )
}
