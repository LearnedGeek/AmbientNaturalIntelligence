using AniRuntime.Core;
using FluentAssertions;

namespace AniRuntime.Tests;

public class VectorMathTests
{
    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        var a = new float[] { 1, 2, 3, 4, 5 };
        VectorMath.CosineSimilarity(a, a).Should().BeApproximately(1.0f, 0.0001f);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        var a = new float[] { 1, 0, 0, 0 };
        var b = new float[] { 0, 1, 0, 0 };
        VectorMath.CosineSimilarity(a, b).Should().BeApproximately(0f, 0.0001f);
    }

    [Fact]
    public void CosineSimilarity_OppositeVectors_ReturnsNegativeOne()
    {
        var a = new float[] { 1, 2, 3 };
        var b = new float[] { -1, -2, -3 };
        VectorMath.CosineSimilarity(a, b).Should().BeApproximately(-1.0f, 0.0001f);
    }

    [Fact]
    public void CosineSimilarity_ZeroVector_ReturnsZeroDenomValue()
    {
        var a = new float[] { 0, 0, 0 };
        var b = new float[] { 1, 2, 3 };
        VectorMath.CosineSimilarity(a, b).Should().Be(0f);
        VectorMath.CosineSimilarity(a, b, zeroDenomValue: 1.0f).Should().Be(1.0f);
    }

    [Fact]
    public void CosineSimilarity_EmptyArrays_ReturnsZeroDenomValue()
    {
        VectorMath.CosineSimilarity(Array.Empty<float>(), Array.Empty<float>()).Should().Be(0f);
    }

    [Fact]
    public void CosineSimilarity_DifferentLengths_ReturnsZeroDenomValue()
    {
        var a = new float[] { 1, 2 };
        var b = new float[] { 1, 2, 3 };
        VectorMath.CosineSimilarity(a, b).Should().Be(0f);
    }

    [Fact]
    public void CosineSimilarity_LargeVectors_MatchesScalar()
    {
        // 768-dimensional vectors (nomic-embed-text output size) — exercises SIMD path
        var rng = new Random(42);
        var a = Enumerable.Range(0, 768).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();
        var b = Enumerable.Range(0, 768).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();

        var result = VectorMath.CosineSimilarity(a, b);

        // Compute expected scalar result for verification
        float dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot  += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        var expected = dot / (MathF.Sqrt(magA) * MathF.Sqrt(magB));

        result.Should().BeApproximately(expected, 0.0001f);
    }

    [Fact]
    public void CosineSimilarity_SmallVectors_WorksWithoutSimd()
    {
        // 4-dimensional vectors (emotional drift vectors) — may not hit SIMD path
        var a = new float[] { 0.7f, 0.3f, 0.1f, 0.5f };
        var b = new float[] { 0.6f, 0.4f, 0.2f, 0.4f };

        var result = VectorMath.CosineSimilarity(a, b);

        // Manual calculation
        float dot = 0.7f*0.6f + 0.3f*0.4f + 0.1f*0.2f + 0.5f*0.4f;
        float magA = MathF.Sqrt(0.7f*0.7f + 0.3f*0.3f + 0.1f*0.1f + 0.5f*0.5f);
        float magB = MathF.Sqrt(0.6f*0.6f + 0.4f*0.4f + 0.2f*0.2f + 0.4f*0.4f);
        var expected = dot / (magA * magB);

        result.Should().BeApproximately(expected, 0.0001f);
    }
}
