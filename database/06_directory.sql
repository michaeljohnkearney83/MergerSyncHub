/*
    06_directory.sql
    A mock corporate directory, standing in for Active Directory / Microsoft Entra ID.

      dbo.DirectoryUser   - people (one row per account)
      dbo.SecurityGroup   - groups, each granting one application role
      dbo.GroupMember     - who is in which group
      dbo.AuditEvent      - every sign-in, joiner, mover and leaver action

    Access works like Entra ID app role assignment: users are put in groups, groups
    map to roles, and the sign-in token carries the roles for the app being signed in to.
    Users themselves are seeded by CorpDirectory.Api on first start, because their
    passwords need hashing in code.

    Re-runnable: drops and recreates everything (and therefore every user account).
*/
IF DB_ID(N'CorpDirectory') IS NULL
    CREATE DATABASE CorpDirectory;
GO
USE CorpDirectory;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

DROP TABLE IF EXISTS dbo.AuditEvent;
DROP TABLE IF EXISTS dbo.GroupMember;
DROP TABLE IF EXISTS dbo.SecurityGroup;
DROP TABLE IF EXISTS dbo.DirectoryUser;
GO

CREATE TABLE dbo.DirectoryUser
(
    UserId              uniqueidentifier NOT NULL CONSTRAINT PK_DirectoryUser PRIMARY KEY
                        CONSTRAINT DF_DirectoryUser_Id DEFAULT (NEWSEQUENTIALID()),
    UserPrincipalName   nvarchar(256)  NOT NULL CONSTRAINT UX_DirectoryUser_Upn UNIQUE,   -- sign-in name
    DisplayName         nvarchar(200)  NOT NULL,
    GivenName           nvarchar(100)  NOT NULL,
    Surname             nvarchar(100)  NOT NULL,
    JobTitle            nvarchar(100)  NULL,
    Department          nvarchar(100)  NULL,
    Office              nvarchar(100)  NULL,
    EmployeeId          nvarchar(20)   NULL,
    AccountEnabled      bit            NOT NULL CONSTRAINT DF_DirectoryUser_Enabled DEFAULT (1),
    PasswordHash        varchar(200)   NOT NULL,   -- PBKDF2; never the password itself
    MustChangePassword  bit            NOT NULL CONSTRAINT DF_DirectoryUser_MustChange DEFAULT (1),
    FailedSignInCount   int            NOT NULL CONSTRAINT DF_DirectoryUser_Failed DEFAULT (0),
    LockedUntilUtc      datetime2(3)   NULL,
    LastSignInUtc       datetime2(3)   NULL,
    PasswordChangedUtc  datetime2(3)   NULL,
    CreatedAtUtc        datetime2(3)   NOT NULL CONSTRAINT DF_DirectoryUser_Created DEFAULT (SYSUTCDATETIME()),
    CreatedBy           nvarchar(256)  NOT NULL,
    UpdatedAtUtc        datetime2(3)   NOT NULL CONSTRAINT DF_DirectoryUser_Updated DEFAULT (SYSUTCDATETIME())
);

CREATE TABLE dbo.SecurityGroup
(
    GroupId      int IDENTITY(1,1) NOT NULL CONSTRAINT PK_SecurityGroup PRIMARY KEY,
    Name         nvarchar(100) NOT NULL CONSTRAINT UX_SecurityGroup_Name UNIQUE,
    Description  nvarchar(300) NOT NULL,
    AppRole      varchar(50)   NOT NULL CONSTRAINT UX_SecurityGroup_Role UNIQUE   -- the role claim this group grants
);

CREATE TABLE dbo.GroupMember
(
    UserId      uniqueidentifier NOT NULL CONSTRAINT FK_GroupMember_User REFERENCES dbo.DirectoryUser (UserId),
    GroupId     int              NOT NULL CONSTRAINT FK_GroupMember_Group REFERENCES dbo.SecurityGroup (GroupId),
    AddedAtUtc  datetime2(3)     NOT NULL CONSTRAINT DF_GroupMember_Added DEFAULT (SYSUTCDATETIME()),
    AddedBy     nvarchar(256)    NOT NULL,
    CONSTRAINT PK_GroupMember PRIMARY KEY (UserId, GroupId)
);

CREATE TABLE dbo.AuditEvent
(
    AuditId     bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AuditEvent PRIMARY KEY,
    AtUtc       datetime2(3)   NOT NULL CONSTRAINT DF_AuditEvent_At DEFAULT (SYSUTCDATETIME()),
    Actor       nvarchar(256)  NOT NULL,   -- who did it (or the user signing in)
    Action      varchar(50)    NOT NULL,
    TargetUpn   nvarchar(256)  NULL,
    Detail      nvarchar(1000) NULL
);
CREATE INDEX IX_AuditEvent_At ON dbo.AuditEvent (AtUtc DESC);
GO

INSERT dbo.SecurityGroup (Name, Description, AppRole) VALUES
 (N'SyncHub Viewers',          N'Read-only access to the SyncHub support dashboard.',                                  'SyncHub.Viewer'),
 (N'SyncHub Support Analysts', N'Correct, replay and ignore integration exceptions.',                                  'SyncHub.Support'),
 (N'SyncHub Data Stewards',    N'Decide possible duplicate clients and maintain practice area mappings.',              'SyncHub.DataSteward'),
 (N'SyncHub Administrators',   N'Full access to the SyncHub support dashboard.',                                       'SyncHub.Admin'),
 (N'Directory Administrators', N'Create and manage user accounts and group membership in the Directory admin centre.', 'Directory.Admin');
GO
