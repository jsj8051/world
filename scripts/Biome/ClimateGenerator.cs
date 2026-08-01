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
    /// 年降水（mm）。
    /// 纬度带：赤道 ~1150mm、30° 副热带 ~400mm、60° ~800mm、极地 ~320mm，
    /// 噪声调制 0.55~1.45，高海拔地形增雨（简化，不做雨影）。
    /// </summary>
    public float ComputePrecipitation(Vector3 pos, float elevNorm)
    {
        Vector3 dir = pos.Normalized();
        float latDeg = Mathf.Abs(Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(dir.Y, -1f, 1f)))); // 0..90

        float equatorBand = 1400f * Mathf.Exp(-latDeg * latDeg / (2f * 12f * 12f));    // σ=12°：赤道 ITCZ（窄，30° 副热带处几乎归零 → 出沙漠带）
        float polarFront = 550f * Mathf.Exp(-(latDeg - 62f) * (latDeg - 62f) / (2f * 14f * 14f)); // 60° 极锋
        float baseP = 150f + equatorBand + polarFront;

        float noise = _precipNoise.GetNoise3D(dir.X, dir.Y, dir.Z);
        float mod = 1f + noise * 0.45f; // 0.55..1.45

        if (elevNorm > 0f)
            baseP *= 1f + Mathf.Min(elevNorm, 1f) * 0.4f; // 地形抬升增雨

        return Mathf.Max(0f, baseP * mod);
    }
}
