using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using ClimaSense.Web.Generated.MLClient.Models;
using Xunit;

namespace ClimaSense.Web.Tests;

/// <summary>
/// Locks the snake_case wire spelling of every enum in the Kiota-generated
/// tree. Each enum value carries an <see cref="EnumMemberAttribute"/>
/// whose <see cref="EnumMemberAttribute.Value"/> is the canonical wire
/// string. Kiota's serializers consume those attributes; no custom
/// <c>JsonConverter</c> is needed for round-trip correctness.
/// </summary>
public sealed class GeneratedEnumWireSpellingTests
{
    private static string WireOf<TEnum>(TEnum value) where TEnum : struct, System.Enum
    {
        var name = value.ToString();
        var field = typeof(TEnum).GetField(name, BindingFlags.Public | BindingFlags.Static)
                    ?? throw new Xunit.Sdk.XunitException(
                        $"{typeof(TEnum).Name}.{name} not found.");

        var attr = field.GetCustomAttribute<EnumMemberAttribute>()
                   ?? throw new Xunit.Sdk.XunitException(
                       $"{typeof(TEnum).Name}.{name} missing [EnumMember].");
        return attr.Value
            ?? throw new Xunit.Sdk.XunitException(
                $"{typeof(TEnum).Name}.{name} has [EnumMember] with null Value.");
    }

    [Fact]
    public void AnomalyType_values_carry_snake_case_wire_spelling()
    {
        Assert.Equal("sensor_failure", WireOf(AnomalyType.Sensor_failure));
        Assert.Equal("regime_shift", WireOf(AnomalyType.Regime_shift));
        Assert.Equal("residual_outlier", WireOf(AnomalyType.Residual_outlier));
    }

    [Fact]
    public void ComfortSeason_values_carry_canonical_wire_spelling()
    {
        Assert.Equal("summer", WireOf(ComfortSeason.Summer));
        Assert.Equal("winter", WireOf(ComfortSeason.Winter));
    }

    [Fact]
    public void ComfortRating_values_carry_canonical_wire_spelling()
    {
        Assert.Equal("excellent", WireOf(ComfortRating.Excellent));
        Assert.Equal("acceptable", WireOf(ComfortRating.Acceptable));
        Assert.Equal("marginal", WireOf(ComfortRating.Marginal));
        Assert.Equal("uncomfortable", WireOf(ComfortRating.Uncomfortable));
    }

    [Fact]
    public void Pattern_values_carry_canonical_wire_spelling()
    {
        Assert.Equal("quiet", WireOf(Pattern.Quiet));
        Assert.Equal("warm", WireOf(Pattern.Warm));
        Assert.Equal("cool", WireOf(Pattern.Cool));
        Assert.Equal("volatile", WireOf(Pattern.Volatile));
    }

    [Fact]
    public void Status_values_carry_canonical_wire_spelling()
    {
        Assert.Equal("ok", WireOf(Status.Ok));
        Assert.Equal("degraded", WireOf(Status.Degraded));
        Assert.Equal("unavailable", WireOf(Status.Unavailable));
    }

    [Fact]
    public void Every_enum_in_the_generated_tree_uses_EnumMember()
    {
        var asm = typeof(AnomalyType).Assembly;
        var enums = asm.GetTypes()
            .Where(t => t.Namespace == "ClimaSense.Web.Generated.MLClient.Models" && t.IsEnum)
            .ToList();

        Assert.NotEmpty(enums);

        foreach (var enumType in enums)
        {
            foreach (var field in enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var attr = field.GetCustomAttribute<EnumMemberAttribute>();
                Assert.True(
                    attr is not null,
                    $"{enumType.Name}.{field.Name} missing [EnumMember] — wire spelling drift.");
                Assert.False(
                    string.IsNullOrEmpty(attr!.Value),
                    $"{enumType.Name}.{field.Name} has [EnumMember] with empty Value.");
            }
        }
    }
}
