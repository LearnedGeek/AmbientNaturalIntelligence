using System.Numerics;

namespace AniRuntime.Core;

/// <summary>
/// SIMD-accelerated vector math utilities shared across the runtime.
/// Feature 9: Consolidates three duplicate scalar cosine similarity implementations
/// into one optimized path using System.Numerics.Vector&lt;float&gt;.
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// Computes cosine similarity between two float arrays.
    /// Uses SIMD acceleration when available (System.Numerics.Vector).
    /// Returns <paramref name="zeroDenomValue"/> when either vector has zero magnitude.
    /// </summary>
    /// <param name="a">First vector.</param>
    /// <param name="b">Second vector.</param>
    /// <param name="zeroDenomValue">Value to return when denominator is zero (default: 0f).</param>
    public static float CosineSimilarity(float[] a, float[] b, float zeroDenomValue = 0f)
    {
        if (a.Length != b.Length || a.Length == 0)
            return zeroDenomValue;

        float dot, magA, magB;

        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            var vDot  = Vector<float>.Zero;
            var vMagA = Vector<float>.Zero;
            var vMagB = Vector<float>.Zero;
            var i = 0;

            // SIMD loop — process Vector<float>.Count elements per iteration
            for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var va = new Vector<float>(a, i);
                var vb = new Vector<float>(b, i);
                vDot  += va * vb;
                vMagA += va * va;
                vMagB += vb * vb;
            }

            dot  = Vector.Dot(vDot,  Vector<float>.One);
            magA = Vector.Dot(vMagA, Vector<float>.One);
            magB = Vector.Dot(vMagB, Vector<float>.One);

            // Scalar remainder
            for (; i < a.Length; i++)
            {
                dot  += a[i] * b[i];
                magA += a[i] * a[i];
                magB += b[i] * b[i];
            }
        }
        else
        {
            // Scalar fallback
            dot = 0f; magA = 0f; magB = 0f;
            for (var i = 0; i < a.Length; i++)
            {
                dot  += a[i] * b[i];
                magA += a[i] * a[i];
                magB += b[i] * b[i];
            }
        }

        var denom = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return denom > 0 ? dot / denom : zeroDenomValue;
    }
}
