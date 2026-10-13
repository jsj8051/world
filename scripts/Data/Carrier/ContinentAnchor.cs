using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用

namespace World.Data;

/// <summary>大陆锚点：中心方向 + 椭圆影响参数（shape = 轴比，rotation = 切平面内轴朝向）+ 海岸性格。</summary>
public sealed class ContinentAnchor
{
	/// <summary>中心单位方向。</summary>
	public Vector3 Dir;
	/// <summary>影响半径（km，球面弧长口径）——"size"。</summary>
	public float RadiusKm;
	/// <summary>shape 各向异性轴（切平面内正交单位向量；即 "rotation" 的载体）。</summary>
	public Vector3 AxisU, AxisV;
	/// <summary>沿 U/V 轴的拉伸比（≥1；1 = 圆形大陆，2 = 明显长条）。</summary>
	public float StretchU, StretchV;
	/// <summary>海岸复杂度乘子（≈[0.5,1.6]）：调制该大陆的半岛/海湾/细节幅度——"coastComplexity"。</summary>
	public float CoastComplexity;
	/// <summary>影响权重（≈[0.8,1.25]）：进影响场 ⇒ 归属 = 加权 Voronoi。</summary>
	public float Weight;
}
