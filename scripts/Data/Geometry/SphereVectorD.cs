using System;

namespace World.Data;

/// <summary>A finite double-precision vector, with no unit-length constraint.</summary>
/// <example><code>var omega = new SphereVectorD(0, 0, 0.01);</code></example>
public sealed class SphereVectorD
{
    /// <summary>Cartesian X component.</summary>
    public double X { get; }
    /// <summary>Cartesian Y component.</summary>
    public double Y { get; }
    /// <summary>Cartesian Z component.</summary>
    public double Z { get; }

    /// <summary>Creates a vector; zero is valid but nonfinite components are rejected.</summary>
    /// <example><code>var stationary = new SphereVectorD(0, 0, 0);</code></example>
    public SphereVectorD(double x, double y, double z)
    {
        X = x; Y = y; Z = z;
        Validate();
    }

    /// <summary>Rechecks finite components without changing their magnitude.</summary>
    /// <example><code>omega.Validate();</code></example>
    public void Validate()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Z))
            throw new ArgumentException("SphereVectorD components must be finite.");
    }
}
