namespace ClimaSense.Monitor.Domain;

public static class Psychrometrics
{
    private const double A = 17.62;
    private const double B = 243.12;

    // Magnus formula: gamma = ln(RH/100) + a*T/(b+T); dewPoint = b*gamma / (a - gamma)
    // humidityPct is clamped to [1, 100] to avoid ln(0).
    public static double DewPointC(double tempC, double humidityPct)
    {
        double rh = Math.Clamp(humidityPct, 1.0, 100.0);
        double gamma = Math.Log(rh / 100.0) + A * tempC / (B + tempC);
        return B * gamma / (A - gamma);
    }

    // How far the air is above its dew point. Small margin => condensation risk.
    public static double CondensationMarginC(double tempC, double humidityPct)
        => tempC - DewPointC(tempC, humidityPct);
}
