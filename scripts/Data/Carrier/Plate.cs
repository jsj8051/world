using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用

namespace World.Data;

/// <summary>板块：种子点位置 + 速度 + 壳型。纯数据载体。</summary>
public struct Plate
{
    /// <summary>板块号（= 板块表下标，与逐格板块号 PlateOfCell 同域）。</summary>
    public int PlateId;
    /// <summary>种子点极角 θ（0 = 北极，π = 南极；弧度）。</summary>
    public float SeedTheta;
    /// <summary>种子点方位角 φ（弧度）。</summary>
    public float SeedPhi;
    /// <summary>速率大小。</summary>
    public float V;
    /// <summary>速度的 θ 方向分量（θ̇，弧度/单位时间）。</summary>
    public float VTheta;
    /// <summary>速度的 φ 方向分量（φ̇，弧度/单位时间）。</summary>
    public float VPhi;
    /// <summary>壳型（生成期判定，本板块的**唯一**陆海身份）：陆壳发一个大陆锚点，洋壳不发 ⇒ 陆地产在陆壳上。</summary>
    public CrustType Crust;
}
