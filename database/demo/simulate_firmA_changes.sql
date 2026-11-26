/*
    Simulates fee earners working in the legacy Firm A system, so the next
    incremental sync has something to pick up. Run it whenever you like during a demo.
*/
USE FirmA_Legacy;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- 25 matters get edited
;WITH pick AS (
    SELECT TOP (25) MATTER_ID FROM dbo.MATTER WHERE STATUS = 'O' ORDER BY NEWID()
)
UPDATE m
   SET DESCRIPTION   = LEFT(m.DESCRIPTION, 450) + ' (updated ' + CONVERT(varchar(16), GETDATE(), 120) + ')',
       LAST_MODIFIED = GETDATE()
  FROM dbo.MATTER m JOIN pick p ON p.MATTER_ID = m.MATTER_ID;

-- 5 matters close
;WITH pick AS (
    SELECT TOP (5) MATTER_ID FROM dbo.MATTER WHERE STATUS = 'O' ORDER BY NEWID()
)
UPDATE m
   SET STATUS = 'C', CLOSE_DATE = CAST(GETDATE() AS date), LAST_MODIFIED = GETDATE()
  FROM dbo.MATTER m JOIN pick p ON p.MATTER_ID = m.MATTER_ID;

-- A new client and matter arrive
DECLARE @Code varchar(10) = 'NEW' + RIGHT(CONVERT(varchar(20), ABS(CHECKSUM(NEWID()))), 4);
INSERT dbo.CLIENT (CLIENT_CODE, CLIENT_NAME, ADDRESS_1, POSTCODE, COUNTRY, OPEN_DATE, STATUS, LAST_MODIFIED)
VALUES (@Code, 'Dockside Ventures ' + @Code + ' Ltd', 'Royal Albert Dock', 'L3 4AF', 'UK', GETDATE(), 'A', GETDATE());

INSERT dbo.MATTER (CLIENT_ID, MATTER_NO, DESCRIPTION, DEPT_CODE, FEE_EARNER, OPEN_DATE, STATUS, LAST_MODIFIED)
SELECT CLIENT_ID, @Code + '.0001', 'Seed funding round', 'CORP', 'PMC', GETDATE(), 'O', GETDATE()
  FROM dbo.CLIENT WHERE CLIENT_CODE = @Code;

-- And one bad record: closed before it was opened
INSERT dbo.MATTER (CLIENT_ID, MATTER_NO, DESCRIPTION, DEPT_CODE, FEE_EARNER, OPEN_DATE, CLOSE_DATE, STATUS, LAST_MODIFIED)
SELECT CLIENT_ID, @Code + '.0002', 'Keyed in wrong', 'LIT', 'DMI', GETDATE(), DATEADD(DAY, -10, GETDATE()), 'C', GETDATE()
  FROM dbo.CLIENT WHERE CLIENT_CODE = @Code;

SELECT 'Changes simulated' AS Result, @Code AS NewClientCode;
GO
