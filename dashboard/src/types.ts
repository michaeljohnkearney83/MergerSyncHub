// Shapes returned by SyncHub.SupportApi (ASP.NET serialises to camelCase)

export interface Summary {
  clients: number
  matters: number
  openExceptions: number
  pendingDuplicates: number
  failedRuns24h: number
  lastSuccessfulSyncUtc: string | null
}

export interface Run {
  syncRunId: number
  source: string
  runType: 'Scheduled' | 'Replay'
  status: 'Running' | 'Succeeded' | 'SucceededWithErrors' | 'Failed'
  startedAtUtc: string
  finishedAtUtc: string | null
  durationSeconds: number | null
  errorMessage: string | null
  correlationId: string
  recordsRead: number
  recordsInserted: number
  recordsUpdated: number
  recordsFailed: number
}

export interface RunEntity {
  entity: string
  sourceTotal: number | null
  recordsRead: number
  recordsStaged: number
  recordsInserted: number
  recordsUpdated: number
  recordsFailed: number
}

export type DeadLetterStatus = 'New' | 'ReplayRequested' | 'Resolved' | 'Ignored'

export interface DeadLetter {
  deadLetterId: number
  source: string
  entity: string
  sourceKey: string
  relatedClientKey: string | null
  errorType: string
  errorMessage: string
  status: DeadLetterStatus
  occurrenceCount: number
  replayAttempts: number
  createdAtUtc: string
  lastSeenAtUtc: string
  resolvedAtUtc: string | null
}

export interface DeadLetterDetail extends DeadLetter {
  firstSyncRunId: number
  lastSyncRunId: number
  payload: string
  payloadEditedBy: string | null
  payloadEditedAtUtc: string | null
  resolutionNote: string | null
}

export interface ErrorTypeCount {
  source: string
  errorType: string
  count: number
}

export interface Duplicate {
  candidateId: number
  score: number
  matchReason: string
  status: 'Pending' | 'ConfirmedDuplicate' | 'NotDuplicate'
  reviewedBy: string | null
  reviewedAtUtc: string | null
  clientIdA: number
  sourceA: string
  keyA: string
  nameA: string
  countryA: string | null
  postCodeA: string | null
  mattersA: number
  clientIdB: number
  sourceB: string
  keyB: string
  nameB: string
  countryB: string | null
  postCodeB: string | null
  mattersB: number
}

export interface ReconciliationRow {
  source: string
  entity: string
  sourceTotal: number
  inHub: number
  openExceptions: number
  ignored: number
  unexplained: number
  status: 'Reconciled' | 'ExplainedByExceptions' | 'Investigate'
  asOfSyncRunId: number
  asOfUtc: string
}

export interface UnmappedCode {
  source: string
  practiceAreaCode: string | null
  matters: number
}

export interface ClientLookup {
  clientId: number
  sourceClientKey: string
  clientName: string
  postCode: string | null
}
