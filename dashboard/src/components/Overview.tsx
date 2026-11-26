import { Fragment, useState } from 'react'
import { api } from '../api'
import type { RunEntity } from '../types'
import { formatDateTime, number, timeAgo, usePolling } from '../util'
import { Empty, ErrorBanner, Panel, SourceTag, StatusBadge } from './ui'

export function SummaryTiles({ onNavigate }: { onNavigate: (tab: 'exceptions' | 'duplicates') => void }) {
  const { data, error } = usePolling(api.summary, [], 5_000)

  return (
    <>
      <ErrorBanner message={error} />
      <div className="tiles">
        <div className="tile">
          <span className="tile-label">Clients in hub</span>
          <span className="tile-value">{data ? number(data.clients) : '…'}</span>
        </div>
        <div className="tile">
          <span className="tile-label">Matters in hub</span>
          <span className="tile-value">{data ? number(data.matters) : '…'}</span>
        </div>
        <button className={`tile tile-button ${data && data.openExceptions > 0 ? 'tile-warn' : ''}`}
                onClick={() => onNavigate('exceptions')}>
          <span className="tile-label">Open exceptions</span>
          <span className="tile-value">{data ? number(data.openExceptions) : '…'}</span>
        </button>
        <button className={`tile tile-button ${data && data.pendingDuplicates > 0 ? 'tile-info' : ''}`}
                onClick={() => onNavigate('duplicates')}>
          <span className="tile-label">Duplicates to review</span>
          <span className="tile-value">{data ? number(data.pendingDuplicates) : '…'}</span>
        </button>
        <div className={`tile ${data && data.failedRuns24h > 0 ? 'tile-bad' : ''}`}>
          <span className="tile-label">Failed runs (24h)</span>
          <span className="tile-value">{data ? number(data.failedRuns24h) : '…'}</span>
        </div>
        <div className="tile">
          <span className="tile-label">Last successful sync</span>
          <span className="tile-value tile-value-small">{data ? timeAgo(data.lastSuccessfulSyncUtc) : '…'}</span>
        </div>
      </div>
    </>
  )
}

export function ReconciliationPanel() {
  const { data, error } = usePolling(api.reconciliation, [], 15_000)

  return (
    <Panel title="Reconciliation: source systems vs hub">
      <ErrorBanner message={error} />
      {data && data.length === 0 && <Empty>No scheduled sync has completed yet.</Empty>}
      {data && data.length > 0 && (
        <table>
          <thead>
            <tr>
              <th>Source</th><th>Entity</th><th className="num">In source</th><th className="num">In hub</th>
              <th className="num">Open exceptions</th><th className="num">Ignored</th><th className="num">Unexplained</th>
              <th>Status</th><th>As of</th>
            </tr>
          </thead>
          <tbody>
            {data.map(r => (
              <tr key={`${r.source}-${r.entity}`}>
                <td><SourceTag source={r.source} /></td>
                <td>{r.entity}s</td>
                <td className="num">{number(r.sourceTotal)}</td>
                <td className="num">{number(r.inHub)}</td>
                <td className="num">{number(r.openExceptions)}</td>
                <td className="num">{number(r.ignored)}</td>
                <td className={`num ${r.unexplained !== 0 ? 'text-bad' : ''}`}>{number(r.unexplained)}</td>
                <td><StatusBadge status={r.status} /></td>
                <td className="muted">{formatDateTime(r.asOfUtc)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <p className="hint">
        In source = In hub + Open exceptions + Ignored. Anything left over is unexplained and needs investigating.
        Small differences can appear briefly when records change between the count and the read.
      </p>
    </Panel>
  )
}

export function RunsPanel() {
  const [hideQuiet, setHideQuiet] = useState(true)
  const [expanded, setExpanded] = useState<number | null>(null)
  const [entities, setEntities] = useState<RunEntity[]>([])
  const { data, error } = usePolling(() => api.runs(hideQuiet), [hideQuiet], 10_000)

  async function toggle(id: number) {
    if (expanded === id) {
      setExpanded(null)
      return
    }
    setExpanded(id)
    setEntities([])
    try {
      setEntities(await api.runEntities(id))
    } catch {
      setEntities([])
    }
  }

  return (
    <Panel
      title="Sync runs"
      actions={
        <label className="checkbox">
          <input type="checkbox" checked={hideQuiet} onChange={e => setHideQuiet(e.target.checked)} />
          Hide runs with no changes
        </label>
      }
    >
      <ErrorBanner message={error} />
      {data && data.length === 0 && <Empty>No runs yet. Is the worker running?</Empty>}
      {data && data.length > 0 && (
        <table>
          <thead>
            <tr>
              <th>Run</th><th>Source</th><th>Type</th><th>Status</th><th>Started</th><th className="num">Secs</th>
              <th className="num">Read</th><th className="num">Inserted</th><th className="num">Updated</th>
              <th className="num">Failed</th>
            </tr>
          </thead>
          <tbody>
            {data.map(r => (
              <Fragment key={r.syncRunId}>
                <tr className="clickable" onClick={() => void toggle(r.syncRunId)}>
                  <td>#{r.syncRunId}</td>
                  <td><SourceTag source={r.source} /></td>
                  <td>{r.runType}</td>
                  <td><StatusBadge status={r.status} /></td>
                  <td>{formatDateTime(r.startedAtUtc)}</td>
                  <td className="num">{r.durationSeconds?.toFixed(1) ?? '—'}</td>
                  <td className="num">{number(r.recordsRead)}</td>
                  <td className="num">{number(r.recordsInserted)}</td>
                  <td className="num">{number(r.recordsUpdated)}</td>
                  <td className={`num ${r.recordsFailed > 0 ? 'text-warn' : ''}`}>{number(r.recordsFailed)}</td>
                </tr>
                {expanded === r.syncRunId && (
                  <tr className="detail-row">
                    <td colSpan={10}>
                      <div className="run-detail">
                        <div><strong>Correlation id</strong> <code>{r.correlationId}</code> <span className="muted">(search the worker logs for this)</span></div>
                        {r.errorMessage && <div className="text-bad"><strong>Error</strong> {r.errorMessage}</div>}
                        {entities.map(e => (
                          <div key={e.entity}>
                            <strong>{e.entity}s</strong>: source holds {e.sourceTotal === null ? 'n/a' : number(e.sourceTotal)}, read {number(e.recordsRead)},
                            staged {number(e.recordsStaged)}, inserted {number(e.recordsInserted)},
                            updated {number(e.recordsUpdated)}, failed {number(e.recordsFailed)}
                          </div>
                        ))}
                      </div>
                    </td>
                  </tr>
                )}
              </Fragment>
            ))}
          </tbody>
        </table>
      )}
    </Panel>
  )
}
