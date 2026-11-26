import { useState } from 'react'
import { api } from '../api'
import type { Duplicate } from '../types'
import { formatDateTime, usePolling } from '../util'
import { Empty, ErrorBanner, Panel, SourceTag, StatusBadge } from './ui'

const FILTERS = ['Pending', 'ConfirmedDuplicate', 'NotDuplicate', 'All'] as const
const FILTER_LABELS: Record<(typeof FILTERS)[number], string> = {
  Pending: 'To review',
  ConfirmedDuplicate: 'Confirmed',
  NotDuplicate: 'Rejected',
  All: 'All',
}

export function DuplicatesPanel({ user, canDecide }: { user: string; canDecide: boolean }) {
  const [filter, setFilter] = useState<(typeof FILTERS)[number]>('Pending')
  const [error, setError] = useState<string | null>(null)
  const { data, error: loadError, reload } = usePolling(() => api.duplicates(filter), [filter], 15_000)

  async function decide(d: Duplicate, decision: Duplicate['status']) {
    if (!user) {
      setError('Sign in first, so decisions are attributed.')
      return
    }
    try {
      await api.decide(d.candidateId, decision, user)
      setError(null)
      await reload()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  return (
    <Panel
      title="Possible duplicate clients"
      actions={
        <div className="segmented">
          {FILTERS.map(f => (
            <button key={f} className={f === filter ? 'active' : ''} onClick={() => setFilter(f)}>{FILTER_LABELS[f]}</button>
          ))}
        </div>
      }
    >
      <p className="hint">
        Suggested by name matching (ignoring punctuation and legal suffixes such as Ltd / Inc) plus postcode.
        Confirming links the records under one master client in reporting; it does not delete anything.
        Pairs found in both legacy firms are shared clients, which matters for conflict checks.
      </p>
      <ErrorBanner message={error ?? loadError} />
      {data && data.length === 0 && <Empty>No pairs in this list.</Empty>}

      <div className="pairs">
        {data?.map(d => (
          <article key={d.candidateId} className="pair">
            <header className="pair-header">
              <span className="score">{Math.round(d.score * 100)}%</span>
              <span className="muted">{d.matchReason}</span>
              <StatusBadge status={d.status} />
            </header>
            <div className="pair-body">
              <ClientCard source={d.sourceA} name={d.nameA} sourceKey={d.keyA} country={d.countryA} postCode={d.postCodeA} matters={d.mattersA} />
              <span className="vs">≈</span>
              <ClientCard source={d.sourceB} name={d.nameB} sourceKey={d.keyB} country={d.countryB} postCode={d.postCodeB} matters={d.mattersB} />
            </div>
            <footer className="button-row">
              {d.status === 'Pending' ? (
                canDecide ? (
                  <>
                    <button className="primary" onClick={() => void decide(d, 'ConfirmedDuplicate')}>Same client</button>
                    <button onClick={() => void decide(d, 'NotDuplicate')}>Different clients</button>
                  </>
                ) : (
                  <span className="muted small">Awaiting a decision from a Data Steward</span>
                )
              ) : (
                <>
                  <span className="muted small">Reviewed by {d.reviewedBy ?? 'unknown'} · {formatDateTime(d.reviewedAtUtc)}</span>
                  {canDecide && <button className="link" onClick={() => void decide(d, 'Pending')}>Undo</button>}
                </>
              )}
            </footer>
          </article>
        ))}
      </div>
    </Panel>
  )
}

function ClientCard(props: {
  source: string; name: string; sourceKey: string; country: string | null; postCode: string | null; matters: number
}) {
  return (
    <div className="client-card">
      <SourceTag source={props.source} />
      <div className="client-name">{props.name}</div>
      <div className="muted small">
        Key <code>{props.sourceKey}</code> · {props.country ?? 'country unknown'} · {props.postCode ?? 'no postcode'}
      </div>
      <div className="small">{props.matters} matter(s)</div>
    </div>
  )
}
