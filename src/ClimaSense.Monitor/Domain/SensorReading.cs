namespace ClimaSense.Monitor.Domain;

/// <summary>One reading. Timestamp is CET wall-clock (DateTimeKind.Unspecified).</summary>
public readonly record struct SensorReading(long Id, DateTime Timestamp, int TemperatureC, int HumidityPct);
