namespace ClimaSense.Monitor.Domain;

public sealed class EnvelopeRange
{
    public double Min { get; set; }
    public double Max { get; set; }
}

public sealed class EnvelopeOptions
{
    public const string SectionName = "Envelope";
    public EnvelopeRange TemperatureRecommended { get; set; } = new() { Min = 18, Max = 27 };
    public EnvelopeRange TemperatureAllowable   { get; set; } = new() { Min = 15, Max = 32 };
    public EnvelopeRange HumidityRecommended    { get; set; } = new() { Min = 20, Max = 80 };
    public EnvelopeRange HumidityAllowable      { get; set; } = new() { Min = 8,  Max = 90 };
    public int FreshnessMinutes { get; set; } = 30;
}
