namespace ClimaSense.Monitor.Domain;

public enum ReadingBand { Recommended = 0, Allowable = 1, OutOfRange = 2 }

public static class BandEvaluator
{
    public static ReadingBand Classify(double value, EnvelopeRange recommended, EnvelopeRange allowable)
    {
        if (value >= recommended.Min && value <= recommended.Max) return ReadingBand.Recommended;
        if (value >= allowable.Min && value <= allowable.Max) return ReadingBand.Allowable;
        return ReadingBand.OutOfRange;
    }

    public static ReadingBand Worst(ReadingBand a, ReadingBand b)
        => (ReadingBand)System.Math.Max((int)a, (int)b);
}
