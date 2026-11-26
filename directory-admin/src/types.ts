export interface UserSummary {
  userId: string
  userPrincipalName: string
  displayName: string
  jobTitle: string | null
  department: string | null
  office: string | null
  accountEnabled: boolean
  mustChangePassword: boolean
  isLocked: boolean
  lastSignInUtc: string | null
  createdAtUtc: string
  groups: string | null
}

export interface UserDetail {
  userId: string
  userPrincipalName: string
  displayName: string
  givenName: string
  surname: string
  jobTitle: string | null
  department: string | null
  office: string | null
  employeeId: string | null
  accountEnabled: boolean
  mustChangePassword: boolean
  failedSignInCount: number
  lockedUntilUtc: string | null
  isLocked: boolean
  lastSignInUtc: string | null
  passwordChangedUtc: string | null
  createdAtUtc: string
  createdBy: string
  updatedAtUtc: string
  groupIds: number[]
}

export interface Profile {
  givenName: string
  surname: string
  jobTitle: string
  department: string
  office: string
  employeeId: string
}

export interface Group {
  groupId: number
  name: string
  description: string
  appRole: string
  members: number
}

export interface AuditEvent {
  auditId: number
  atUtc: string
  actor: string
  action: string
  targetUpn: string | null
  detail: string | null
}

export interface DirectoryOptions {
  offices: string[]
  departments: string[]
  emailDomain: string
}

export interface WithTemporaryPassword {
  user: UserDetail
  temporaryPassword: string
}
