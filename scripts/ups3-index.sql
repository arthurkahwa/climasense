-- Covering index for ClimaSense range/aggregation queries on ups3.dbo.tbl_sensor_data.
-- Run once as a privileged login (e.g. <your-db-user>). Idempotent.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
              WHERE name = 'IX_tbl_sensor_data_sensor_dateTime'
                AND object_id = OBJECT_ID('dbo.tbl_sensor_data'))
    CREATE NONCLUSTERED INDEX IX_tbl_sensor_data_sensor_dateTime
        ON dbo.tbl_sensor_data (sensor_dateTime)
        INCLUDE (temperature, humidity);
