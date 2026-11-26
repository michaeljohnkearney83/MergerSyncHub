import { useState } from 'react'
import { api } from '../api'
import type { UnmappedCode } from '../types'
import { number, usePolling } from '../util'
import { Empty, ErrorBanner, Panel, SourceTag } from './ui'

/**
 * Department / practice group codes that have no mapping to the combined firm's practice areas.
 * Matters keep their raw code, so adding a mapping fixes reporting on the next refresh
 * without re-syncing anything.
 */
export function MappingsPanel({ canEdit }: { canEdit: boolean }) {
  const unmapped = usePolling(api.unmapped, [], 30_000)
  const areas = usePolling(api.practiceAreas, [], 60_000)

  return (
    <Panel title="Unmapped practice area codes">
      <p className="hint">
        Each legacy firm used its own department codes. These codes have no mapping yet, so their matters
        show as “Unmapped” in Power BI and SSRS. Choose an existing practice area or type a new one.
      </p>
      <ErrorBanner message={unmapped.error ?? areas.error} />
      {unmapped.data && unmapped.data.length === 0 && <Empty>Every code is mapped.</Empty>}
      {unmapped.data && unmapped.data.length > 0 && (
        <table>
          <thead>
            <tr><th>Source</th><th>Code</th><th className="num">Matters</th><th>Map to</th></tr>
          </thead>
          <tbody>
            {unmapped.data.map(u => (
              <MappingRow key={`${u.source}-${u.practiceAreaCode ?? ''}`} row={u} areas={areas.data ?? []} canEdit={canEdit}
                          onSaved={() => void unmapped.reload()} />
            ))}
          </tbody>
        </table>
      )}
    </Panel>
  )
}

function MappingRow({ row, areas, canEdit, onSaved }: {
  row: UnmappedCode; areas: string[]; canEdit: boolean; onSaved: () => void
}) {
  const [value, setValue] = useState('')
  const [error, setError] = useState<string | null>(null)
  const listId = `areas-${row.source}-${row.practiceAreaCode ?? 'none'}`

  async function save() {
    if (!row.practiceAreaCode || !value.trim()) return
    try {
      await api.mapPracticeArea(row.source, row.practiceAreaCode, value.trim())
      onSaved()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  return (
    <tr>
      <td><SourceTag source={row.source} /></td>
      <td><code>{row.practiceAreaCode ?? '(blank)'}</code></td>
      <td className="num">{number(row.matters)}</td>
      <td>
        {!canEdit ? (
          <span className="muted small">Mapping needs the SyncHub Data Stewards group</span>
        ) : row.practiceAreaCode ? (
          <div className="inline-form">
            <input list={listId} value={value} onChange={e => setValue(e.target.value)} placeholder="Practice area" />
            <datalist id={listId}>{areas.map(a => <option key={a} value={a} />)}</datalist>
            <button disabled={!value.trim()} onClick={() => void save()}>Save</button>
            {error && <span className="text-bad small">{error}</span>}
          </div>
        ) : (
          <span className="muted small">Blank codes need fixing in the source system</span>
        )}
      </td>
    </tr>
  )
}
