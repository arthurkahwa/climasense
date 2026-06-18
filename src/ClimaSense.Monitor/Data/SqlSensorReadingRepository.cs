using ClimaSense.Monitor.Domain;
using Dapper;
using Microsoft.Data.SqlClient;

namespace ClimaSense.Monitor.Data;

public sealed class SqlSensorReadingRepository(string connectionString) : ISensorReadingRepository
{
    public async Task<SensorReading?> GetLatestAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT TOP 1 id AS Id, sensor_dateTime AS SensorDateTime, temperature AS Temperature, humidity AS Humidity FROM dbo.tbl_sensor_data ORDER BY sensor_dateTime DESC;";
        await using var con = new SqlConnection(connectionString);
        var row = await con.QueryFirstOrDefaultAsync<LatestRow>(new CommandDefinition(sql, commandTimeout: 15, cancellationToken: ct));
        return row is null ? null : new SensorReading(row.Id, row.SensorDateTime, row.Temperature, row.Humidity);
    }

    public async Task<IReadOnlyList<SeriesPoint>> GetSeriesAsync(DateTime fromCet, DateTime toCet, int bucketMinutes, CancellationToken ct = default)
    {
        const string sql = """
            SELECT DATEADD(MINUTE,(DATEDIFF(MINUTE,'2000-01-01',sensor_dateTime)/@bucket)*@bucket,'2000-01-01') AS BucketStartCet,
                   AVG(CAST(temperature AS float)) AS AvgTemp, MIN(temperature) AS MinTemp, MAX(temperature) AS MaxTemp,
                   AVG(CAST(humidity   AS float)) AS AvgHumidity, MIN(humidity) AS MinHumidity, MAX(humidity) AS MaxHumidity,
                   COUNT(*) AS Cnt
            FROM dbo.tbl_sensor_data
            WHERE sensor_dateTime >= @from AND sensor_dateTime < @to
            GROUP BY DATEADD(MINUTE,(DATEDIFF(MINUTE,'2000-01-01',sensor_dateTime)/@bucket)*@bucket,'2000-01-01')
            ORDER BY BucketStartCet;
            """;
        await using var con = new SqlConnection(connectionString);
        var rows = await con.QueryAsync<SeriesRow>(new CommandDefinition(sql,
            new { from = fromCet, to = toCet, bucket = bucketMinutes }, commandTimeout: 30, cancellationToken: ct));
        return rows.Select(r => new SeriesPoint(r.BucketStartCet, r.AvgTemp, r.MinTemp, r.MaxTemp,
                                                r.AvgHumidity, r.MinHumidity, r.MaxHumidity, r.Cnt)).ToList();
    }

    public async Task<IReadOnlyList<DailyAggregate>> GetDailyAggregatesAsync(DateTime fromCet, DateTime toCet, CancellationToken ct = default)
    {
        const string sql = """
            SELECT CAST(sensor_dateTime AS date) AS DateValue,
                   AVG(CAST(temperature AS float)) AS AvgTemp, MIN(temperature) AS MinTemp, MAX(temperature) AS MaxTemp,
                   AVG(CAST(humidity   AS float)) AS AvgHumidity, MIN(humidity) AS MinHumidity, MAX(humidity) AS MaxHumidity,
                   COUNT(*) AS Cnt
            FROM dbo.tbl_sensor_data
            WHERE sensor_dateTime >= @from AND sensor_dateTime < @to
            GROUP BY CAST(sensor_dateTime AS date)
            ORDER BY DateValue;
            """;
        await using var con = new SqlConnection(connectionString);
        var rows = await con.QueryAsync<DailyRow>(new CommandDefinition(sql,
            new { from = fromCet, to = toCet }, commandTimeout: 30, cancellationToken: ct));
        return rows.Select(r => new DailyAggregate(DateOnly.FromDateTime(r.DateValue), r.AvgTemp, r.MinTemp, r.MaxTemp,
                                                   r.AvgHumidity, r.MinHumidity, r.MaxHumidity, r.Cnt)).ToList();
    }

    public async Task<IReadOnlyList<SensorReading>> GetRawAsync(DateTime fromCet, DateTime toCet, int maxPoints, CancellationToken ct = default)
    {
        const string sql = """
            SELECT TOP (@max) id AS Id, sensor_dateTime AS SensorDateTime, temperature AS Temperature, humidity AS Humidity
            FROM dbo.tbl_sensor_data
            WHERE sensor_dateTime >= @from AND sensor_dateTime < @to
            ORDER BY sensor_dateTime;
            """;
        await using var con = new SqlConnection(connectionString);
        var rows = await con.QueryAsync<LatestRow>(new CommandDefinition(sql,
            new { from = fromCet, to = toCet, max = maxPoints }, commandTimeout: 30, cancellationToken: ct));
        return rows.Select(r => new SensorReading(r.Id, r.SensorDateTime, r.Temperature, r.Humidity)).ToList();
    }

    sealed class LatestRow { public long Id { get; set; } public DateTime SensorDateTime { get; set; } public int Temperature { get; set; } public int Humidity { get; set; } }
    sealed class SeriesRow { public DateTime BucketStartCet { get; set; } public double AvgTemp { get; set; } public int MinTemp { get; set; } public int MaxTemp { get; set; } public double AvgHumidity { get; set; } public int MinHumidity { get; set; } public int MaxHumidity { get; set; } public int Cnt { get; set; } }
    sealed class DailyRow { public DateTime DateValue { get; set; } public double AvgTemp { get; set; } public int MinTemp { get; set; } public int MaxTemp { get; set; } public double AvgHumidity { get; set; } public int MinHumidity { get; set; } public int MaxHumidity { get; set; } public int Cnt { get; set; } }
}
