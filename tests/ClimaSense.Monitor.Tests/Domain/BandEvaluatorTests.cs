using ClimaSense.Monitor.Domain;
using Xunit;

namespace ClimaSense.Monitor.Tests.Domain;

public class BandEvaluatorTests
{
    static readonly EnvelopeRange Rec = new() { Min = 18, Max = 27 };
    static readonly EnvelopeRange All = new() { Min = 15, Max = 32 };

    [Theory]
    [InlineData(20, ReadingBand.Recommended)]
    [InlineData(18, ReadingBand.Recommended)]
    [InlineData(27, ReadingBand.Recommended)]
    [InlineData(16, ReadingBand.Allowable)]
    [InlineData(32, ReadingBand.Allowable)]
    [InlineData(35, ReadingBand.OutOfRange)]
    [InlineData(10, ReadingBand.OutOfRange)]
    public void Classify_buckets_value(double value, ReadingBand expected)
        => Assert.Equal(expected, BandEvaluator.Classify(value, Rec, All));

    [Fact]
    public void Worst_returns_more_severe_band()
        => Assert.Equal(ReadingBand.OutOfRange,
            BandEvaluator.Worst(ReadingBand.Recommended, ReadingBand.OutOfRange));
}
