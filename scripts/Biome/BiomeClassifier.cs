namespace World.Biome;

/// <summary>
/// 生物群系分类器（纯函数，线程安全）。
/// 输入：归一化海拔（-1..1，0=海平面）、年均温（°C）、年降水（mm）。
/// 矩阵：4 温度带（极地/寒带/温带/热带）× 4 湿度带（干旱/半干旱/半湿润/湿润），
/// 海洋和高海拔单独处理。
/// </summary>
public static class BiomeClassifier
{
    /// <summary>海拔归一化值 &lt; 此值 → 深海。</summary>
    public const float DeepOceanLevel = -0.1f;
    /// <summary>海拔归一化值 &lt; 此值 → 浅海/海洋。</summary>
    public const float OceanLevel = 0.02f;
    /// <summary>高于此归一化海拔（-1..1）→ 山地（Alpine 或冰雪）。0.5 ≈ 5km。</summary>
    public const float AlpineLevel = 0.5f;

    public static BiomeType Classify(float elevNorm, float tempC, float precipMm)
    {
        // ── 海洋 ──
        if (elevNorm < DeepOceanLevel)
            return BiomeType.DeepOcean;
        if (elevNorm < OceanLevel)
            return BiomeType.Ocean;

        // ── 高海拔山地（在温度带判断之前，山地覆盖一切）──
        // 雪线附近（0~-8°C）是岩石高山带；更冷则是冰雪。
        if (elevNorm > AlpineLevel)
            return tempC < -8f ? BiomeType.IceCap : BiomeType.Alpine;

        // ── 温度带 × 湿度带 ──
        if (tempC < -10f)
            return BiomeType.IceCap; // 极地

        if (tempC < 5f) // 寒带
        {
            if (precipMm < 300f) return BiomeType.ColdDesert;
            if (precipMm < 800f) return BiomeType.Tundra;
            return BiomeType.Taiga;
        }

        if (tempC < 18f) // 温带
        {
            if (precipMm < 350f) return BiomeType.Desert;
            if (precipMm < 700f) return BiomeType.TemperateGrassland;
            return BiomeType.TemperateForest;
        }

        // 热带
        if (precipMm < 400f) return BiomeType.Desert;
        if (precipMm < 1000f) return BiomeType.Savanna;
        if (precipMm < 1500f) return BiomeType.TropicalDryForest;
        return BiomeType.TropicalForest;
    }
}
