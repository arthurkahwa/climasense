-- Optional: least-privilege login to replace <your-db-user> in the connection string.
-- Change the password before running. Idempotent.
USE master;
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = 'climasense_ro')
    CREATE LOGIN [climasense_ro] WITH PASSWORD = 'CHANGE_ME_Strong#2026', CHECK_POLICY = ON;
GO
USE ups3;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'climasense_ro')
    CREATE USER [climasense_ro] FOR LOGIN [climasense_ro];
GRANT SELECT ON dbo.tbl_sensor_data TO [climasense_ro];
GO
