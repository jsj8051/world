using Godot;

namespace World.MapGen;

/// <summary>
/// 场数值 ↔ 存档 byte 编解码器（2026-08-19，P0-② 场契约收编）。
///
/// 历史教训（双重归一化全黄案 2026-08-05）：月降水从"绝对 mm"改"比例"后，byte 化/解码
/// 在 MapGenerator/MapViewer/MonsoonDiag/WildCropsSystem 等多处手写公式，改语义时漏一处
/// 就全图层错误。本类是全部 byte 编解码的唯一入口——改编码语义只动这里。
///
/// 约定（v3.7/v3.8 存档格式）：
///   比例场（季风强度 0~1、月降水比例 Σ=1）：byte = round(v×255)，0↔255 线性
///   温度场（°C → byte，−60~60°C）：byte = round((t+60)/120×255)
/// </summary>
public static class FieldCodec
{
    /// <summary>温度场编码范围（°C，对称：-60~60 → 0-255）。</summary>
    public const float TempMinC = -60f;
    public const float TempMaxC = 60f;
    public const float TempSpanC = TempMaxC - TempMinC;   // 120

    /// <summary>比例场（0..1）→ byte（0..255；clamp 防越界）。季风强度/月降水比例用。</summary>
    public static byte RatioToByte(float v) => (byte)(Mathf.Clamp(v, 0f, 1f) * 255f);

    /// <summary>byte → 比例（0..1）。与 RatioToByte 互逆。</summary>
    public static float ByteToRatio(byte b) => b / 255f;

    /// <summary>温度（°C，−60~60）→ byte。月温度场用。</summary>
    public static byte TempToByte(float tC) => (byte)(Mathf.Clamp((tC - TempMinC) / TempSpanC, 0f, 1f) * 255f);

    /// <summary>byte → 温度（°C）。与 TempToByte 互逆。</summary>
    public static float ByteToTemp(byte b) => b / 255f * TempSpanC + TempMinC;

    /// <summary>月降水比例 byte → 当月降水（mm，×年降水；比例 Σ=1 语义）。</summary>
    public static float ByteMonthPrecipToMm(byte b, float annualMm) => ByteToRatio(b) * annualMm;
}
