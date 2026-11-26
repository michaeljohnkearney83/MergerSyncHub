/*
    02_firmA_legacy.sql
    Legacy UK practice management database ("Firm A").

    This is deliberately written like an older vendor schema:
      - UPPER_CASE names, varchar everywhere, local-time datetime columns
      - no foreign key between MATTER and CLIENT (so orphan matters exist)
      - no index on LAST_MODIFIED (see database/perf for the fix and why it matters)
      - messy data: duplicate clients, inconsistent country values, a missing
        client name, close dates before open dates, unmapped department codes

    Date literals use the unambiguous YYYYMMDD format. 'YYYY-MM-DD' is read as
    YYYY-DD-MM for datetime when the login language is British English.
*/
USE FirmA_Legacy;
GO

DROP TABLE IF EXISTS dbo.MATTER;
DROP TABLE IF EXISTS dbo.CLIENT;
GO

CREATE TABLE dbo.CLIENT
(
    CLIENT_ID      int IDENTITY(1000, 1) NOT NULL CONSTRAINT PK_CLIENT PRIMARY KEY,
    CLIENT_CODE    varchar(10)  NOT NULL,
    CLIENT_NAME    varchar(200) NULL,
    ADDRESS_1      varchar(200) NULL,
    POSTCODE       varchar(12)  NULL,
    COUNTRY        varchar(50)  NULL,
    OPEN_DATE      datetime     NULL,
    STATUS         char(1)      NOT NULL CONSTRAINT DF_CLIENT_STATUS DEFAULT ('A'),   -- A = active, C = closed
    LAST_MODIFIED  datetime     NOT NULL CONSTRAINT DF_CLIENT_LAST_MODIFIED DEFAULT (GETDATE())  -- UK local time
);

CREATE TABLE dbo.MATTER
(
    MATTER_ID      int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_MATTER PRIMARY KEY,
    CLIENT_ID      int          NULL,          -- no FK: legacy systems often lack one
    MATTER_NO      varchar(20)  NOT NULL,
    DESCRIPTION    varchar(500) NULL,
    DEPT_CODE      varchar(10)  NULL,
    FEE_EARNER     varchar(10)  NULL,
    OPEN_DATE      datetime     NULL,
    CLOSE_DATE     datetime     NULL,
    STATUS         char(1)      NOT NULL CONSTRAINT DF_MATTER_STATUS DEFAULT ('O'),   -- O = open, C = closed
    LAST_MODIFIED  datetime     NOT NULL CONSTRAINT DF_MATTER_LAST_MODIFIED DEFAULT (GETDATE())
);
GO

/* ---------------------------------------------------------------------------
   Hand-written clients: each one demonstrates a specific data problem
--------------------------------------------------------------------------- */
INSERT dbo.CLIENT (CLIENT_CODE, CLIENT_NAME, ADDRESS_1, POSTCODE, COUNTRY, OPEN_DATE, STATUS, LAST_MODIFIED)
VALUES
 ('ACME001', 'ACME HOLDINGS LTD',                        '1 Mann Island',         'L3 1BP',   'United Kingdom', '20150302', 'A', '20240110 09:15'),
 ('ACME002', 'Acme Holdings Limited ',                   '1 Mann Island',         'L3 1BP',   'UK',             '20190719', 'A', '20240211 14:02'), -- duplicate of ACME001
 ('MERS001', 'Mersey Logistics plc',                     'Wapping Quay',          'L3 4AF',   'GB',             '20160512', 'A', '20231103 10:00'),
 ('MERS002', 'MERSEY LOGISTICS PUBLIC LIMITED COMPANY',  'Wapping Quay',          'L3 4AF',   'England',        '20200901', 'A', '20240314 16:45'), -- duplicate of MERS001
 ('BRIG001', 'Brightwater Pharma Ltd',                   '12 Hills Road',         'CB2 1TN',  'United Kingdom', '20170123', 'A', '20240402 08:30'),
 ('NORT001', 'Northbank Capital LLP',                    '30 Fleet Street',       'EC4Y 1AA', 'United Kingdom', '20180630', 'A', '20240520 11:11'),
 ('HALC001', 'Halcyon Media Group Ltd',                  '5 Deansgate',           'M3 2BW',   'United Kingdom', '20161010', 'C', '20221201 17:20'),
 ('ORIO001', 'Orion Renewables Limited',                 'Royal Liver Building',  'L3 1HU',   'UK',             '20210214', 'A', '20240618 09:00'),
 ('STJM001', 'The St James Family Office',               '7 St James''s Square',  'SW1Y 4JU', 'United Kingdom', '20151105', 'A', '20240229 13:37'),
 ('STJM002', 'St. James Family Office Ltd',              '7 St James Square',     'SW1Y 4JU', NULL,             '20220401', 'A', '20240707 07:07'), -- duplicate of STJM001
 ('KEST001', 'Kestrel Aerospace Ltd',                    'Hangar 4, Speke',       'L24 1YD',  'United Kingdom', '20190303', 'A', '20240808 12:00'), -- also a Firm B client
 ('VANT001', 'Vantage Biotech Ltd',                      'Oxford Science Park',   'OX4 4GA',  'United Kingdom', '20200115', 'A', '20240909 10:10'), -- also a Firm B client
 ('ALDR001', NULL,                                       'Unknown',               NULL,       NULL,             '20230505', 'A', '20241001 15:00'), -- missing name -> dead letter
 ('PENN001', 'Pennine Water Services Ltd',               'Moor Lane',             'HX1 1AA',  NULL,             '20180808', 'A', '20240303 09:45'),
 ('HARB001', 'Harbourside Hotels Ltd',                   'Royal Albert Dock',     'L3 4AF',   'United Kingdom', '20170707', 'A', '20240101 00:00'),
 ('GRAN001', 'Granite Bay Partners LLP',                 '1 Chiswell Street',     'EC1Y 4UP', 'United Kingdom', '20190505', 'A', '20240505 10:30');  -- also a Firm B client
GO

/* ---------------------------------------------------------------------------
   Generated clients: 24 x 15 = 360 more, with inconsistent suffixes/countries
--------------------------------------------------------------------------- */
;WITH p AS (
    SELECT i, w FROM (VALUES
        (1,'Atlas'),(2,'Beacon'),(3,'Cobalt'),(4,'Delta'),(5,'Ember'),(6,'Falcon'),
        (7,'Granite'),(8,'Harbour'),(9,'Iris'),(10,'Juniper'),(11,'Keystone'),(12,'Lumen'),
        (13,'Meridian'),(14,'Nova'),(15,'Onyx'),(16,'Pioneer'),(17,'Quartz'),(18,'Riverside'),
        (19,'Summit'),(20,'Thames'),(21,'Union'),(22,'Vertex'),(23,'Willow'),(24,'Zenith')) v(i, w)
), s AS (
    SELECT i, w FROM (VALUES
        (1,'Capital'),(2,'Developments'),(3,'Engineering'),(4,'Foods'),(5,'Healthcare'),
        (6,'Holdings'),(7,'Insurance'),(8,'Logistics'),(9,'Media'),(10,'Partners'),
        (11,'Properties'),(12,'Retail'),(13,'Software'),(14,'Technologies'),(15,'Ventures')) v(i, w)
), combos AS (
    SELECT ROW_NUMBER() OVER (ORDER BY p.i, s.i) AS n, p.w AS pw, s.w AS sw
    FROM p CROSS JOIN s
)
INSERT dbo.CLIENT (CLIENT_CODE, CLIENT_NAME, ADDRESS_1, POSTCODE, COUNTRY, OPEN_DATE, STATUS, LAST_MODIFIED)
SELECT
    UPPER(LEFT(pw, 3)) + UPPER(LEFT(sw, 1)) + RIGHT('000' + CAST(n AS varchar(4)), 3),
    CASE n % 4
        WHEN 0 THEN pw + ' ' + sw + ' Ltd'
        WHEN 1 THEN pw + ' ' + sw + ' Limited'
        WHEN 2 THEN UPPER(pw + ' ' + sw + ' plc')
        ELSE        pw + ' ' + sw + ' LLP'
    END,
    CAST(n AS varchar(5)) + ' ' + pw + ' Street',
    CHOOSE(n % 10 + 1, 'L1 8JQ','L2 2DP','M1 1AE','EC2V 7HH','B3 2HB','LS1 4AP','CB2 1TN','SW1A 1AA','G2 1DY','BS1 4DJ'),
    CHOOSE(n % 5 + 1, 'United Kingdom','UK','GB','United Kingdom','England'),
    DATEADD(DAY, (n * 7919) % 3650, '20150101'),
    CASE WHEN n % 13 = 0 THEN 'C' ELSE 'A' END,
    DATEADD(MINUTE, (n * 104729) % 525600, '20250101')
FROM combos;
GO

/* ---------------------------------------------------------------------------
   Generated matters: roughly 75,000 rows, enough to make indexing matter
--------------------------------------------------------------------------- */
;WITH nums AS (
    SELECT TOP (400) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT dbo.MATTER (CLIENT_ID, MATTER_NO, DESCRIPTION, DEPT_CODE, FEE_EARNER, OPEN_DATE, CLOSE_DATE, STATUS, LAST_MODIFIED)
SELECT
    c.CLIENT_ID,
    c.CLIENT_CODE + '.' + RIGHT('0000' + CAST(nums.n AS varchar(4)), 4),
    CHOOSE(x.k % 8 + 1,
        'Commercial contract review', 'Shareholder dispute', 'Acquisition of target company',
        'Trade mark portfolio management', 'Employment tribunal claim', 'Property portfolio refinancing',
        'Trust restructuring', 'Regulatory investigation'),
    CASE WHEN x.k % 97 = 0 THEN 'MISC'                                      -- unmapped department code
         ELSE CHOOSE(x.k % 7 + 1, 'LIT','CORP','IP','PCS','RE','EMP','TAX') END,
    CHOOSE(x.k % 10 + 1, 'PMC','DMI','JSM','AKH','RLT','SWB','HDP','KTO','LNG','EJR'),
    o.OpenDate,
    cl.CloseDate,
    CASE WHEN cl.CloseDate IS NULL THEN 'O' ELSE 'C' END,
    DATEADD(MINUTE, x.k % 1440, CASE WHEN lm.d > '20261101' THEN CAST('20261101' AS datetime) ELSE lm.d END)
FROM dbo.CLIENT c
JOIN nums ON nums.n <= (c.CLIENT_ID * 37) % 400 + 1
CROSS APPLY (SELECT c.CLIENT_ID * 1000 + nums.n AS k) x
CROSS APPLY (SELECT DATEADD(DAY, (x.k * 31) % 3600, CAST('20160101' AS datetime)) AS OpenDate) o
CROSS APPLY (SELECT CASE WHEN x.k % 5 < 3 THEN
                        CASE WHEN x.k % 211 = 0 THEN DATEADD(DAY, -30, o.OpenDate)   -- bad data: closed before opened
                             ELSE DATEADD(DAY, 30 + x.k % 900, o.OpenDate) END
                    END AS Cand) cd
CROSS APPLY (SELECT CASE WHEN cd.Cand <= '20261101' THEN cd.Cand END AS CloseDate) cl
CROSS APPLY (SELECT DATEADD(DAY, x.k % 90, COALESCE(cl.CloseDate, o.OpenDate)) AS d) lm
WHERE c.CLIENT_NAME IS NOT NULL;
GO

/* Orphan matters: client 999999 does not exist, and one has no client at all */
INSERT dbo.MATTER (CLIENT_ID, MATTER_NO, DESCRIPTION, DEPT_CODE, FEE_EARNER, OPEN_DATE, STATUS, LAST_MODIFIED)
VALUES
 (999999, 'ZZZZ001.0001', 'Migrated from old system - client unknown', 'LIT',  'PMC', '20230101', 'O', '20250301 10:00'),
 (999999, 'ZZZZ001.0002', 'Migrated from old system - client unknown', 'CORP', 'DMI', '20230115', 'O', '20250301 10:01'),
 (NULL,   'ZZZZ001.0003', 'Matter opened without client link',         'IP',   'JSM', '20240620', 'O', '20250415 16:20');

/* Matters for ALDR001 (the client with no name). They will be dead-lettered as
   orphans until the client record is fixed and replayed: a cascading failure. */
INSERT dbo.MATTER (CLIENT_ID, MATTER_NO, DESCRIPTION, DEPT_CODE, FEE_EARNER, OPEN_DATE, STATUS, LAST_MODIFIED)
SELECT CLIENT_ID, 'ALDR001.0001', 'Supply agreement negotiation', 'CORP', 'AKH', '20230510', 'O', '20241002 09:00'
FROM dbo.CLIENT WHERE CLIENT_CODE = 'ALDR001'
UNION ALL
SELECT CLIENT_ID, 'ALDR001.0002', 'Debt recovery', 'LIT', 'RLT', '20230601', 'O', '20241002 09:05'
FROM dbo.CLIENT WHERE CLIENT_CODE = 'ALDR001';
GO

SELECT (SELECT COUNT(*) FROM dbo.CLIENT) AS Clients, (SELECT COUNT(*) FROM dbo.MATTER) AS Matters;
GO
