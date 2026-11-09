using System;

namespace World.Data;

/// <summary>A finite unit direction in double precision; construction never normalizes invalid input.</summary>
/// <example><code>var north = new SpherePointD(0, 0, 1); north.Validate();</code></example>
public sealed class SpherePointD
{
    /// <summary>Cartesian X component.</summary>
    public double X { get; }
    /// <summary>Cartesian Y component.</summary>
    public double Y { get; }
    /// <summary>Cartesian Z component.</summary>
    public double Z { get; }

    /// <summary>Creates a direction whose length differs from one by at most 1e-12.</summary>
    /// <exception cref="ArgumentException">Components are nonfinite or the direction is not unit length.</exception>
    /// <example><code>var east = new SpherePointD(1, 0, 0);</code></example>
    public SpherePointD(double x, double y, double z)
    {
        X = x; Y = y; Z = z;
        Validate();
    }

    /// <summary>Checks finite components and the unit-length invariant again at an ownership boundary.</summary>
    /// <example><code>point.Validate();</code></example>
    public void Validate()
    {
        double length = Math.Sqrt(X * X + Y * Y + Z * Z);
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Z)
            || !double.IsFinite(length) || Math.Abs(length - 1) > 1e-12)
            throw new ArgumentException("SpherePointD must be finite and unit length.");
    }
}
