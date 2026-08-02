using Godot;
using World.Surface;

namespace World.Biome;

/// <summary>
/// 气候场生成（简化物理，策略游戏精度）：
///   温度 = 纬度基准（赤道热→极地冷）× 大陆性噪声 + 海拔递减（6.5°C/km）
///   降水 = 纬度带曲线（赤道 ITCZ 多雨 + 60° 极锋多雨，30° 副热带干旱）× 噪声调制
/// 纯查询接口，FastNoiseLite 只读调用，可在后台线程/Parallel.For 使用
/// （与 SurfaceGenerator 相同模式）。
/// </summary>
public class ClimateGenerator
{
    private readonly FastNoiseLite _tempNoise;
    private readonly FastNoiseLite _precipNoise;

    public ClimateGenerator(int seed)
    {
        _tempNoise = new FastNoiseLite();
        _tempNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _tempNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _tempNoise.FractalOctaves = 3;
        _tempNoise.Frequency = 0.0006f; // 波长 ~1 万 km：大尺度大陆性差异
        _tempNoise.Seed = seed ^ 0x1A2B3C;

        _precipNoise = new FastNoiseLite();
        _precipNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        _precipNoise.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        _precipNoise.FractalOctaves = 4;
        _precipNoise.Frequency = 0.0012f; // 波长 ~5000 km：区域降水差异
        _precipNoise.Seed = seed ^ 0x4D5E6F;
    }

    /// <summary>
    /// 年均温（°C）。
    /// elevNorm 为归一化海拔（-1..1，0=海平面；与 PlanetColors/Classifier 约定一致）；
    /// 海拔按 e=1.0 → 10km 折算（项目 elevationScaleKm=10），每 km 递减 6.5°C。
    /// </summary>
    public float ComputeTemperature(Vector3 pos, float elevNorm)
    {
        Vector3 dir = pos.Normalized();
        float lat = Mathf.Asin(Mathf.Clamp(dir.Y, -1f, 1f)); // -π/2..π/2
        float cosLat = Mathf.Cos(lat);                        // 1 赤道 → 0 极地

        // 纬度基准：赤道 ≈ +26°C，极地 ≈ -22°C（cosLat^1.1 让高纬度降温更快）
        float baseT = 48f * Mathf.Pow(cosLat, 1.1f) - 22f;

        // 大陆性噪声：±7°C
        float noise = _tempNoise.GetNoise3D(dir.X, dir.Y, dir.Z) * 7f;

        // 海拔递减：6.5°C/km，最高 10km（归一化 1.0）
        float elevKm = Mathf.Max(0f, elevNorm) * 10f;
        return baseT + noise - 6.5f * elevKm;
    }

    /// <summary>
    /// 年降水（mm），含盛行风机制：
    ///   1. 纬度带基准：赤道 ITCZ 多雨 + 60° 极锋多雨，30° 副热带干旱
    ///   2. 盛行风湿润度：风从海洋来（上风向是海）→ 增雨；从大陆来 → 减雨
    ///   3. 雨影：风爬坡（迎风坡）增雨，下沉（背风坡）减雨——风向相对地形
    ///   4. 噪声调制
    /// sampleElev：球面点 → 归一化海拔（-1..1，&lt;0=海洋），用于判断上风向海陆
    /// </summary>
    public float ComputePrecipitation(Vector3 pos, float elevNorm, System.Func<Vector3, float> sampleElev = null)
    {
        Vector3 dir = pos.Normalized();
        float latDeg = Mathf.Abs(Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(dir.Y, -1f, 1f)))); // 0..90

        float equatorBand = 1400f * Mathf.Exp(-latDeg * latDeg / (2f * 12f * 12f));    // σ=12°：赤道 ITCZ（窄，30° 副热带处几乎归零 → 出沙漠带）
        float polarFront = 550f * Mathf.Exp(-(latDeg - 62f) * (latDeg - 62f) / (2f * 14f * 14f)); // 60° 极锋
        // 副热带高压下沉带（~15-38°）：干燥空气下沉 → 降水压制（真实撒哈拉/中亚/澳洲内陆 <100mm）
        //   在 26° 最强（压到 ~90mm），向赤道/极地两侧渐弱
        float subtropical = 1f - 0.85f * Mathf.Exp(-(latDeg - 26f) * (latDeg - 26f) / (2f * 9f * 9f));
        // 基准底压低：150 → 60（极地/副极地本来也少雨，之前 150 底让全球偏湿）
        float baseP = (60f + equatorBand + polarFront) * subtropical;

        float noise = _precipNoise.GetNoise3D(dir.X, dir.Y, dir.Z);
        // ⚠️ 2026-08-02：噪声从 ±45% 降到 ±12%——之前噪声摆动比盛行风修正（±35%）还大，
        //   把信风带/雨影的物理信号淹没了。现在纬度带基准是骨架、盛行风是主要调节、噪声只是小扰动。
        float mod = 1f + noise * 0.12f; // 0.88..1.12

        if (elevNorm > 0f)
            baseP *= 1f + Mathf.Min(elevNorm, 1f) * 0.4f; // 地形抬升增雨

        // ── 盛行风修正（2026-08-02，WindField）──
        if (sampleElev != null)
        {
            // 1. 海洋湿润度：上风向海陆（-1 大陆风干燥 ~ +1 海洋风湿润）
            //    2026-08-02 增强：±35% → ±55%（噪声调小后，盛行风是主要调节）
            float maritime = WindField.MaritimeScore(dir, elevNorm, sampleElev);
            baseP *= 1f + maritime * 0.55f;   // ±55%

            // 2. 雨影：风向与地形梯度方向比较
            //    风爬坡（上风向低、下风向高）→ 迎风坡增雨；下坡（背风）→ 减雨
            Vector3 wind = WindField.WindAt(dir);
            // 0.12rad ≈ 6.9°（跨过顶点胞，雨影才有效）
            // windComp > 0 = 风爬坡（下风向海拔 > 上风向）→ 增雨
            float windComp = (sampleElev((dir + wind * 0.12f).Normalized())   // 下风向海拔
                           - sampleElev((dir - wind * 0.12f).Normalized()))  // 上风向海拔
                           * 5f;  // 放大
            baseP *= 1f + Mathf.Clamp(windComp, -0.65f, 0.65f);   // 迎风 +65% / 背风 -65%
        }

        return Mathf.Max(0f, baseP * mod);
    }
}
