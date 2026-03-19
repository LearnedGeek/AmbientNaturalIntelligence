using AniRuntime.Core.Models;
using FluentAssertions;

namespace AniRuntime.Tests;

public class RegisterFamilyTests
{
    [Theory]
    [InlineData("Playfulness", RegisterFamily.Playfulness)]
    [InlineData("playfulness", RegisterFamily.Playfulness)]
    [InlineData("Mischief", RegisterFamily.Playfulness)]
    [InlineData("Teasing", RegisterFamily.Playfulness)]
    [InlineData("Wit", RegisterFamily.Playfulness)]
    [InlineData("Delight", RegisterFamily.Delight)]
    [InlineData("Joy", RegisterFamily.Delight)]
    [InlineData("Amusement", RegisterFamily.Delight)]
    [InlineData("Giddiness", RegisterFamily.Delight)]
    [InlineData("Curiosity", RegisterFamily.Curiosity)]
    [InlineData("Wonder", RegisterFamily.Curiosity)]
    [InlineData("Investigation", RegisterFamily.Curiosity)]
    [InlineData("Tenderness", RegisterFamily.Tenderness)]
    [InlineData("Admiration", RegisterFamily.Tenderness)]
    [InlineData("Protective", RegisterFamily.Tenderness)]
    [InlineData("Longing", RegisterFamily.Longing)]
    [InlineData("Missing", RegisterFamily.Longing)]
    [InlineData("Ache", RegisterFamily.Longing)]
    [InlineData("Wistful", RegisterFamily.Longing)]
    [InlineData("Melancholy", RegisterFamily.Longing)]
    [InlineData("Desire", RegisterFamily.Warmth)]
    [InlineData("Warmth", RegisterFamily.Warmth)]
    [InlineData("Wanting", RegisterFamily.Warmth)]
    [InlineData("Existential", RegisterFamily.Existential)]
    [InlineData("Awareness", RegisterFamily.Existential)]
    [InlineData("Clarity", RegisterFamily.Existential)]
    [InlineData("Frustration", RegisterFamily.Hurt)]
    [InlineData("Hurt", RegisterFamily.Hurt)]
    [InlineData("Withdrawal", RegisterFamily.Hurt)]
    [InlineData("Worry", RegisterFamily.Concern)]
    [InlineData("Concern", RegisterFamily.Concern)]
    [InlineData("Anxiety", RegisterFamily.Concern)]
    public void ToRegisterFamily_MapsCorrectly(string register, RegisterFamily expected)
    {
        ImpactCategoryDefaults.ToRegisterFamily(register).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("UnknownRegister")]
    public void ToRegisterFamily_DefaultsToLonging(string? register)
    {
        ImpactCategoryDefaults.ToRegisterFamily(register!).Should().Be(RegisterFamily.Longing);
    }

    [Fact]
    public void AllRegisterFamilies_AreMappable()
    {
        // Every RegisterFamily enum value should be reachable from at least one register string
        var reachable = new HashSet<RegisterFamily>();
        var testRegisters = new[]
        {
            "Playfulness", "Delight", "Curiosity", "Tenderness",
            "Longing", "Desire", "Existential", "Frustration", "Worry"
        };

        foreach (var reg in testRegisters)
            reachable.Add(ImpactCategoryDefaults.ToRegisterFamily(reg));

        reachable.Should().HaveCount(Enum.GetValues<RegisterFamily>().Length,
            "every register family should be reachable from at least one register string");
    }

    [Fact]
    public void EmotionalContribution_RegisterProperty_DefaultsToWistful()
    {
        var contribution = new EmotionalContribution();
        contribution.Register.Should().Be("Wistful");
    }

    [Fact]
    public void EmotionalContribution_RegisterProperty_Settable()
    {
        var contribution = new EmotionalContribution { Register = "Playfulness" };
        contribution.Register.Should().Be("Playfulness");
        ImpactCategoryDefaults.ToRegisterFamily(contribution.Register)
            .Should().Be(RegisterFamily.Playfulness);
    }
}
