using World.Biome;

namespace World.LogicGrid;

/// <summary>
/// 存档布局描述（2026-08-19，P1-① 布局驱动存档）。
///
/// 历史教训：.gmp 自然段布局在 GameMapArchive.WriteBody/ReadBody 手写三份
/// （写/读/长度公式），CivMapArchive 又硬编码 naturalLen = 53 + 94n——布局一改
/// 就断链（psi 段错位案、正文错位内存炸弹案都是此类）。本表是自然段布局的
/// 【唯一长度来源】：BodyLength 从字段表推导，任何新增/改动字段自动传导。
///
/// 字段顺序必须与 WriteBody 严格一致（数组字段 = 元素数 × n）。
/// </summary>
public static class ArchiveLayout
{
    public enum FType : byte
    {
        U8,        // 1B × n
        I32,       // 4B × n
        F32,       // 4B × n
        V3,        // 12B × n（3 float）
        Month2D,   // 12 月 × n（1B 每元素）
    }

    public readonly record struct Field(string Name, FType Type, bool PerVertex);

    /// <summary>自然段固定头部字段（GridN 起 → Verts 前；不计入 n 倍率）。</summary>
    private static readonly Field[] HeaderFields =
    {
        new("GridN", FType.I32, false),
        new("N", FType.I32, false),
        new("Seed", FType.I32, false),
        new("RadiusKm", FType.F32, false),
        new("Prograde", FType.U8, false),
        new("RotationSpeed", FType.F32, false),
        new("AxialTilt", FType.F32, false),
        new("Insolation", FType.F32, false),
        new("MinElev", FType.F32, false), new("MaxElev", FType.F32, false),
        new("MinTemp", FType.F32, false), new("MaxTemp", FType.F32, false),
        new("MinPrecip", FType.F32, false), new("MaxPrecip", FType.F32, false),
    };

    /// <summary>每顶点字段（全部 [4B×N]/[1B×N] 数据段）。</summary>
    private static readonly Field[] PerVertexFields =
    {
        new("Verts", FType.V3, true),
        new("Elev", FType.F32, true),
        new("Temp", FType.F32, true),
        new("Precip", FType.F32, true),
        new("Biome", FType.U8, true),
        new("RiverLevel", FType.U8, true),
        new("RiverFlow", FType.I32, true),
        new("RiverVolume", FType.F32, true),
        new("LakeLevel", FType.U8, true),
        new("MineralLevel", FType.U8, true),
        new("SoilLevel", FType.U8, true),
        new("MonsoonLevel", FType.U8, true),
        new("MonthPrecip", FType.Month2D, true),
        new("MonthTemp", FType.Month2D, true),
        new("CurrentDirs", FType.V3, true),
        new("CurrentWarmth", FType.F32, true),
        new("CurrentStrength", FType.F32, true),
        new("Psi", FType.F32, true),        // v2 起（ver≥2）
        new("Province", FType.I32, true),
        new("Country", FType.I32, true),
    };

    private static int SizeOf(FType t) => t switch
    {
        FType.U8 => 1,
        FType.I32 => 4,
        FType.F32 => 4,
        FType.V3 => 12,
        FType.Month2D => MonsoonSystem.MonthCount,
        _ => 0,
    };

    /// <summary>自然段总长度（字节）：固定头 + Σ(每顶点字段 × n)。ver&lt;2 无 Psi。</summary>
    public static long BodyLength(int n, int ver)
    {
        long len = 0;
        foreach (var f in HeaderFields) len += SizeOf(f.Type);
        foreach (var f in PerVertexFields)
        {
            if (f.Name == "Psi" && ver < 2) continue;   // v1 旧档无流函数
            len += (long)SizeOf(f.Type) * n;
        }
        return len;
    }
}
