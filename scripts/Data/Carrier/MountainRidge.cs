using System;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用

namespace World.Data;

// 数据层 `World.Data`（2026-10-09 建立）：见 ContinentAnchor.cs 头部的入层判据与依赖方向。
/// <summary>一条山脊：曲线行走点列（~20 km 步长）+ **逐点高度**（轴向 profile 的离散载体）。</summary>
public sealed class MountainRidge
{
	public Vector3[] Points = Array.Empty<Vector3>();
	public float[] PointHeightM = Array.Empty<float>();   // 沿轴高度（含宏观 profile 与支脉衰减）
	public bool IsBranch;
	public float SigmaKm;
	public Vector3 Center = Vector3.Zero;
	public float CapRadiusRad;
}
