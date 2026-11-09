using System;

namespace World.Data;

/// <summary>A double-precision unit quaternion; no rotation or integration behavior is provided.</summary>
/// <example><code>RotationD initial = RotationD.Identity;</code></example>
public sealed class RotationD
{
    /// <summary>The identity orientation.</summary>
    public static RotationD Identity { get; } = new(0, 0, 0, 1);
    /// <summary>Quaternion X component.</summary>
    public double X { get; }
    /// <summary>Quaternion Y component.</summary>
    public double Y { get; }
    /// <summary>Quaternion Z component.</summary>
    public double Z { get; }
    /// <summary>Quaternion W component.</summary>
    public double W { get; }

    /// <summary>Creates an orientation whose length differs from one by at most 1e-12.</summary>
    /// <exception cref="ArgumentException">The quaternion is nonfinite or not normalized.</exception>
    /// <example><code>var initial = new RotationD(0, 0, 0, 1);</code></example>
    public RotationD(double x, double y, double z, double w)
    {
        X = x; Y = y; Z = z; W = w;
        Validate();
    }

    /// <summary>Rechecks the unit quaternion invariant without silently normalizing it.</summary>
    /// <example><code>orientation.Validate();</code></example>
    public void Validate()
    {
        double length = Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Z)
            || !double.IsFinite(W) || !double.IsFinite(length) || Math.Abs(length - 1) > 1e-12)
            throw new ArgumentException("RotationD must be finite and unit length.");
    }
}
