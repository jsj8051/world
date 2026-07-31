using Godot;

/// <summary>
/// Shared planet coloring utilities.
/// </summary>
public static class PlanetColors
{
    /// <summary>
    /// Elevation color ramp (normalized [-1, 1]): deep ocean → ocean → beach → lowland → highland → snow.
    /// </summary>
    public static Color ElevationToColor(float e)
    {
        if (e < -0.2f)
        {
            float t = Mathf.Clamp((-e - 0.2f) / 0.8f, 0f, 1f);
            return new Color(0.02f, 0.10f, 0.25f).Lerp(new Color(0.06f, 0.35f, 0.60f), 1f - t);
        }
        if (e < 0.0f)
        {
            float t = (e + 0.2f) / 0.2f;
            return new Color(0.06f, 0.35f, 0.60f).Lerp(new Color(0.70f, 0.65f, 0.40f), t);
        }
        if (e < 0.3f)
        {
            float t = e / 0.3f;
            return new Color(0.70f, 0.65f, 0.40f).Lerp(new Color(0.30f, 0.65f, 0.10f), t);
        }
        if (e < 0.6f)
        {
            float t = (e - 0.3f) / 0.3f;
            return new Color(0.30f, 0.65f, 0.10f).Lerp(new Color(0.50f, 0.50f, 0.08f), t);
        }
        float s = Mathf.Clamp((e - 0.6f) / 0.4f, 0f, 1f);
        return new Color(0.50f, 0.50f, 0.08f).Lerp(new Color(0.95f, 0.97f, 1.00f), s);
    }
}
