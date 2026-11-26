import { useEffect, useState } from 'react'
import { api } from '../api'
import type { ClientLookup } from '../types'

/*
 * Guided correction form for a dead-lettered source record.
 *
 * The record stays in the source system's own JSON shape (that is what the worker replays),
 * but people edit it through labelled fields instead of raw JSON:
 *   - only the fields relevant to the error are shown (with "show all fields" for the rest)
 *   - key fields are locked so a record can't be redirected by accident
 *   - validation mirrors the C# mappers, so a saved fix won't fail the same way again
 */

type Role = 'key' | 'name' | 'clientRef' | 'matterNumber' | 'openDate' | 'closeDate' | 'status' | 'other'

interface FieldDef {
  key: string
  label: string
  role: Role
  required?: boolean
  options?: { value: string; label: string }[]
}

interface Schema {
  dateStyle: 'datetime' | 'date'      // Firm A stores 2024-01-15T00:00:00, Firm B 2024-01-15
  numericClientRef: boolean           // Firm A client ids are integers, Firm B's are strings like C-10001
  fields: FieldDef[]
}

const SCHEMAS: Record<string, Schema> = {
  'FirmA:Client': {
    dateStyle: 'datetime',
    numericClientRef: true,
    fields: [
      { key: 'clientId', label: 'Client id', role: 'key' },
      { key: 'clientCode', label: 'Client code', role: 'key' },
      { key: 'clientName', label: 'Client name', role: 'name', required: true },
      { key: 'address1', label: 'Address', role: 'other' },
      { key: 'postcode', label: 'Postcode', role: 'other' },
      { key: 'country', label: 'Country', role: 'other' },
      { key: 'openDate', label: 'Opened', role: 'openDate' },
      { key: 'status', label: 'Status', role: 'status', options: [{ value: 'A', label: 'Active' }, { value: 'C', label: 'Closed' }] },
      { key: 'lastModified', label: 'Last modified (UK time)', role: 'key' },
    ],
  },
  'FirmA:Matter': {
    dateStyle: 'datetime',
    numericClientRef: true,
    fields: [
      { key: 'matterId', label: 'Matter id', role: 'key' },
      { key: 'clientId', label: 'Client', role: 'clientRef', required: true },
      { key: 'matterNo', label: 'Matter number', role: 'matterNumber', required: true },
      { key: 'description', label: 'Description', role: 'other' },
      { key: 'deptCode', label: 'Department code', role: 'other' },
      { key: 'feeEarner', label: 'Fee earner', role: 'other' },
      { key: 'openDate', label: 'Opened', role: 'openDate' },
      { key: 'closeDate', label: 'Closed', role: 'closeDate' },
      { key: 'status', label: 'Status', role: 'status', options: [{ value: 'O', label: 'Open' }, { value: 'C', label: 'Closed' }] },
      { key: 'lastModified', label: 'Last modified (UK time)', role: 'key' },
    ],
  },
  'FirmB:Client': {
    dateStyle: 'date',
    numericClientRef: false,
    fields: [
      { key: 'id', label: 'Client id', role: 'key' },
      { key: 'name', label: 'Client name', role: 'name', required: true },
      { key: 'country', label: 'Country', role: 'other' },
      { key: 'postalCode', label: 'Postal code', role: 'other' },
      { key: 'openedDate', label: 'Opened', role: 'openDate' },
      { key: 'status', label: 'Status', role: 'status', options: [{ value: 'active', label: 'Active' }, { value: 'inactive', label: 'Inactive' }] },
      { key: 'lastModifiedUtc', label: 'Last modified (UTC)', role: 'key' },
    ],
  },
  'FirmB:Matter': {
    dateStyle: 'date',
    numericClientRef: false,
    fields: [
      { key: 'id', label: 'Matter id', role: 'key' },
      { key: 'clientId', label: 'Client', role: 'clientRef', required: true },
      { key: 'matterNumber', label: 'Matter number', role: 'matterNumber', required: true },
      { key: 'title', label: 'Title', role: 'other' },
      { key: 'practiceGroup', label: 'Practice group', role: 'other' },
      { key: 'responsibleAttorney', label: 'Responsible attorney', role: 'other' },
      { key: 'openedDate', label: 'Opened', role: 'openDate' },
      { key: 'closedDate', label: 'Closed', role: 'closeDate' },
      { key: 'status', label: 'Status', role: 'status', options: [{ value: 'open', label: 'Open' }, { value: 'closed', label: 'Closed' }] },
      { key: 'lastModifiedUtc', label: 'Last modified (UTC)', role: 'key' },
    ],
  },
}

/** Which fields matter for each kind of failure */
const FOCUS: Record<string, Role[]> = {
  MissingClientName: ['name'],
  MissingClientReference: ['clientRef'],
  OrphanMatter: ['clientRef'],
  MissingMatterNumber: ['matterNumber'],
  InvalidDate: ['openDate', 'closeDate', 'status'],
  CloseDateBeforeOpenDate: ['openDate', 'closeDate', 'status'],
}

export type JsonRecord = Record<string, unknown>

/** The editable fields for a source and entity (e.g. FirmA client), or undefined if the form doesn't support it. */
export function schemaFor(source: string, entity: string): Schema | undefined {
  return SCHEMAS[`${source}:${entity}`]
}

/** Parses the payload into an object the form can edit, or null if it isn't a JSON object. */
export function parseRecord(payload: string): JsonRecord | null {
  try {
    const value: unknown = JSON.parse(payload)
    return value !== null && typeof value === 'object' && !Array.isArray(value) ? (value as JsonRecord) : null
  } catch {
    return null
  }
}

/* ---------------- Dates ---------------- */

/** Returns yyyy-MM-dd if the value is a real calendar date (rejects 2023-02-30 and 31/12/2022). */
function isoDatePart(value: unknown): string | null {
  if (typeof value !== 'string') return null
  const m = /^(\d{4})-(\d{2})-(\d{2})(T00:00:00(\.0+)?)?$/.exec(value.trim())
  if (!m) return null
  const [y, mo, d] = [Number(m[1]), Number(m[2]), Number(m[3])]
  const date = new Date(Date.UTC(y, mo - 1, d))
  const valid = date.getUTCFullYear() === y && date.getUTCMonth() === mo - 1 && date.getUTCDate() === d
  return valid ? `${m[1]}-${m[2]}-${m[3]}` : null
}

const isBlank = (v: unknown) => v === null || v === undefined || (typeof v === 'string' && v.trim() === '')

/* ---------------- Validation (mirrors FirmAMapper / FirmBMapper) ---------------- */

/**
 * Checks a corrected record with the same rules as the worker's mappers, so support can fix every problem
 * before replaying instead of finding out from another failure.
 * @returns Error messages by field name; empty when the record is valid.
 */
export function validateRecord(schema: Schema, record: JsonRecord): Record<string, string> {
  const errors: Record<string, string> = {}

  for (const f of schema.fields) {
    const value = record[f.key]
    if (f.required && isBlank(value)) errors[f.key] = `${f.label} is required.`

    if ((f.role === 'openDate' || f.role === 'closeDate') && !isBlank(value) && isoDatePart(value) === null)
      errors[f.key] = `"${String(value)}" is not a valid date.`

    if (f.role === 'clientRef' && !isBlank(value) && schema.numericClientRef && !Number.isInteger(value))
      errors[f.key] = 'Firm A client ids are whole numbers.'
  }

  const open = schema.fields.find(f => f.role === 'openDate')
  const close = schema.fields.find(f => f.role === 'closeDate')
  if (open && close && !errors[open.key] && !errors[close.key]) {
    const o = isoDatePart(record[open.key])
    const c = isoDatePart(record[close.key])
    if (o && c && c < o) errors[close.key] = 'The close date is before the open date.'
  }

  return errors
}

/* ---------------- Form ---------------- */

/** A form for correcting a dead letter's payload field by field, highlighting the fields the error type points at. */
export function RecordForm({ source, entity, errorType, record, errors, readOnly, onChange }: {
  source: string
  entity: string
  errorType: string
  record: JsonRecord
  errors: Record<string, string>
  readOnly: boolean
  onChange: (record: JsonRecord) => void
}) {
  const schema = schemaFor(source, entity)!
  const focus = FOCUS[errorType]
  const [showAll, setShowAll] = useState(!focus)

  const visible = schema.fields.filter(f =>
    showAll || (focus ?? []).includes(f.role) || Boolean(errors[f.key]))

  const set = (key: string, value: unknown) => onChange({ ...record, [key]: value })

  return (
    <div className="record-form">
      {visible.map(f => (
        <div key={f.key} className={`form-row ${errors[f.key] ? 'has-error' : ''}`}>
          <label className="form-label" htmlFor={`f-${f.key}`}>
            {f.label}{f.required && <span className="required"> *</span>}
            {f.role === 'key' && <span className="lock" title="Identity fields can't be changed">🔒</span>}
          </label>
          <Field field={f} schema={schema} source={source} value={record[f.key]}
                 disabled={readOnly || f.role === 'key'} onChange={v => set(f.key, v)} />
          {errors[f.key] && <div className="field-error">{errors[f.key]}</div>}
        </div>
      ))}
      {focus && (
        <button type="button" className="link small" onClick={() => setShowAll(s => !s)}>
          {showAll ? 'Show only the fields that need fixing' : `Show all ${schema.fields.length} fields`}
        </button>
      )}
    </div>
  )
}

function Field({ field, schema, source, value, disabled, onChange }: {
  field: FieldDef
  schema: Schema
  source: string
  value: unknown
  disabled: boolean
  onChange: (value: unknown) => void
}) {
  const id = `f-${field.key}`

  if (field.role === 'openDate' || field.role === 'closeDate') {
    const iso = isoDatePart(value)
    return (
      <div className="date-field">
        <input id={id} type="date" disabled={disabled} value={iso ?? ''}
               onChange={e => onChange(e.target.value === ''
                 ? null
                 : schema.dateStyle === 'datetime' ? `${e.target.value}T00:00:00` : e.target.value)} />
        {!isBlank(value) && iso === null && (
          <span className="muted small">Current value: <code>{String(value)}</code></span>
        )}
        {!disabled && !isBlank(value) && <button type="button" className="link small" onClick={() => onChange(null)}>Clear</button>}
      </div>
    )
  }

  if (field.options) {
    return (
      <select id={id} disabled={disabled} value={typeof value === 'string' ? value : ''}
              onChange={e => onChange(e.target.value)}>
        {field.options.map(o => <option key={o.value} value={o.value}>{o.label} ({o.value})</option>)}
      </select>
    )
  }

  if (field.role === 'clientRef') {
    return <ClientPicker id={id} source={source} numeric={schema.numericClientRef}
                         value={value} disabled={disabled} onChange={onChange} />
  }

  return (
    <input id={id} disabled={disabled} value={isBlank(value) ? '' : String(value)}
           onChange={e => onChange(e.target.value === '' ? null : e.target.value)} />
  )
}

/** Client key input with a search over clients already in the hub for the same source. */
function ClientPicker({ id, source, numeric, value, disabled, onChange }: {
  id: string
  source: string
  numeric: boolean
  value: unknown
  disabled: boolean
  onChange: (value: unknown) => void
}) {
  const [search, setSearch] = useState('')
  const [results, setResults] = useState<ClientLookup[]>([])
  const [searchError, setSearchError] = useState<string | null>(null)

  useEffect(() => {
    if (search.trim().length < 2) {
      setResults([])
      return
    }
    const timer = window.setTimeout(() => {
      api.searchClients(source, search.trim())
        .then(r => { setResults(r); setSearchError(null) })
        .catch(() => setSearchError('Client search is unavailable (restart the Support API to enable it). Type the client key instead.'))
    }, 300)
    return () => window.clearTimeout(timer)
  }, [search, source])

  const toValue = (text: string): unknown =>
    text.trim() === '' ? null : numeric && /^\d+$/.test(text.trim()) ? Number(text.trim()) : text.trim()

  return (
    <div className="client-picker">
      <input id={id} disabled={disabled} placeholder={numeric ? 'e.g. 1000' : 'e.g. C-10001'}
             value={isBlank(value) ? '' : String(value)} onChange={e => onChange(toValue(e.target.value))} />
      {!disabled && (
        <>
          <input className="client-search" placeholder="…or search the hub by client name"
                 value={search} onChange={e => setSearch(e.target.value)} />
          {searchError && <div className="muted small">{searchError}</div>}
          {results.length > 0 && (
            <ul className="client-results">
              {results.map(c => (
                <li key={c.clientId}>
                  <button type="button" onClick={() => { onChange(toValue(c.sourceClientKey)); setSearch(''); setResults([]) }}>
                    <strong>{c.clientName}</strong> <code>{c.sourceClientKey}</code>
                    {c.postCode && <span className="muted"> · {c.postCode}</span>}
                  </button>
                </li>
              ))}
            </ul>
          )}
          {search.trim().length >= 2 && results.length === 0 && !searchError && (
            <div className="muted small">No matching clients in the hub for {source}.</div>
          )}
        </>
      )}
    </div>
  )
}
