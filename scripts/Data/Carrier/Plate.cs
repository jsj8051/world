using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用

namespace World.Data;

/// <summary>板块：种子点位置 + 欧拉矢量 + 密度。壳型是逐格事实，不在此。</summary>
public struct Plate
{
    /// <summary>板块号（= 板块表下标，与逐格板块号 PlateOfCell 同域）。</summary>
    public int PlateId;
    /// <summary>种子点极角 θ（0 = 北极，π = 南极；弧度）。</summary>
    public float SeedTheta;
    /// <summary>种子点方位角 φ（弧度）。</summary>
    public float SeedPhi;
    /// <summary>欧拉矢量：方向 = 旋转的欧拉极轴，模 = 角速率；整块绕它做刚体转动，
    /// 逐格速度 = <c>Omega × 格心方向</c>（角速度逐板块恒定，线速度随位置变）。</summary>
    public Vector3 Omega;
    /// <summary>密度（生成期随机量）。只用于汇聚边界上分上下盘：密度小的当上盘。</summary>
    public float Density;
}
