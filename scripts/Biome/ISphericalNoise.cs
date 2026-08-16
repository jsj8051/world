using Godot;

namespace World.Biome;

/// <summary>
/// 球面 3D 噪声采样抽象（纯托管端口，2026-08 引擎适配器重构）。
/// 作用：把 ClimateGenerator 的物理公式与其噪声源解耦——
///   · 引擎环境：<see cref="FastNoiseLiteNoise"/>（封装 FastNoiseLite）
///   · 测试环境：注入纯托管实现（恒零/确定性伪噪声），无需 Godot 引擎
/// 端口签名保持 Vector3（纯托管数学类型），适配器负责与引擎差异。
/// </summary>
public interface ISphericalNoise
{
    /// <summary>采样单位球方向 p 处的噪声（返回值域与实现相关：FastNoiseLite = -1..1）。</summary>
    float Sample(Vector3 p);
}
