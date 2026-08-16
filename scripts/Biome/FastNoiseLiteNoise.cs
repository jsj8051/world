using Godot;

namespace World.Biome;

/// <summary>
/// ISphericalNoise 的 Godot 引擎实现（适配器）：封装 FastNoiseLite。
/// ⚠️ 只能在引擎进程内构造/调用（FastNoiseLite 是引擎原生类；无引擎测试进程会 0xC0000005 崩溃）。
/// ClimateGenerator 构造传 null 走本类（生产默认路径）；测试注入纯托管实现绕过。
/// 初始参数与原 ClimateGenerator 内联 FastNoiseLite 配置完全一致（行为不变，纯提取）。
/// </summary>
public sealed class FastNoiseLiteNoise : ISphericalNoise
{
    private readonly FastNoiseLite _noise;

    /// <summary>温度噪声（低频 Fbm，波长 ~1 万 km：大尺度大陆性差异）。</summary>
    public static FastNoiseLiteNoise CreateTemperature(int seed)
    {
        var n = new FastNoiseLite();
        n.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        n.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        n.FractalOctaves = 3;
        n.Frequency = 0.0006f; // 波长 ~1 万 km
        n.Seed = seed ^ 0x1A2B3C;
        return new FastNoiseLiteNoise(n);
    }

    /// <summary>降水噪声（Fbm，波长 ~5000 km：区域降水差异）。</summary>
    public static FastNoiseLiteNoise CreatePrecipitation(int seed)
    {
        var n = new FastNoiseLite();
        n.NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth;
        n.FractalType = FastNoiseLite.FractalTypeEnum.Fbm;
        n.FractalOctaves = 4;
        n.Frequency = 0.0012f; // 波长 ~5000 km
        n.Seed = seed ^ 0x4D5E6F;
        return new FastNoiseLiteNoise(n);
    }

    private FastNoiseLiteNoise(FastNoiseLite noise) => _noise = noise;

    public float Sample(Vector3 p) => _noise.GetNoise3D(p.X, p.Y, p.Z);
}
