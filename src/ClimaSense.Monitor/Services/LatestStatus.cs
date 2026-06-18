using ClimaSense.Monitor.Domain;

namespace ClimaSense.Monitor.Services;

public readonly record struct LatestStatus(
    SensorReading Reading, ReadingBand TempBand, ReadingBand HumidityBand, ReadingBand Overall,
    int MinutesOld, bool IsStale);
