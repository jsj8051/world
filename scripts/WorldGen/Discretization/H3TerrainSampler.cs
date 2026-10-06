using System;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Spatial;    // Ball（H3 球壳数据层）
using World.Utils.H3;

namespace World.WorldGen;

// 世界生成空间 · 离散化层（决策原典 §四/§五）：连续海拔场 → H3 逐格数组。
//   六边形只是采样结果——本类是「连续世界」与「H3 世界」之间唯一的桥；
//   场本身不知道 H3 存在 ⇒ 换分辨率/res 升级不需要动任何生成算法（原典 §八/§九 的解耦）。
// 多点采样（原典 §五）：格心 + 该格 6 角点（H3 CellToVertexes），加权平均——
//   防止"格心恰好落在山脊上 ⇒ 单格突兀跳变"；角点方向由 Ball.VertexPositions 归一化（同球半径尺度）。
// 确定性：采样点集由格 id 决定（与线程/次序无关）；加权平均按固定顺序累加（心 → 角点升序）。
/// <summary>
/// H3 地形采样器：把连续场离散化到 Ball 的逐格数组（顺序与 CellDirs 对齐）。
/// </summary>
public sealed class H3TerrainSampler
{
	/// <summary>采样模式：单点（格心） / 多点（格心 + 角点加权平均）。</summary>
	public enum Mode { CenterOnly, CenterAndCorners }

	readonly Ball _ball;

	/// <summary>格心权重（多点模式）：角点合计分走 1−centerWeight（6 角点均分）。默认 0.5；
	/// 1.0 = 退化为单点。越大越信格心、越少边界平滑。</summary>
	public float CenterWeight { get; set; } = 0.5f;

	public H3TerrainSampler(Ball ball) => _ball = ball ?? throw new ArgumentNullException(nameof(ball));

	/// <summary>全量采样：返回逐格海拔（米）。</summary>
	public float[] SampleField(SphericalField field, Mode mode = Mode.CenterAndCorners)
	{
		if (field == null) throw new ArgumentNullException(nameof(field));
		var dirs = _ball.CellDirs;
		int n = dirs.Length;
		var elev = new float[n];

		if (mode == Mode.CenterOnly || CenterWeight >= 1f)
		{
			for (int i = 0; i < n; i++) elev[i] = field.Sample(dirs[i]);
			return elev;
		}

		var cornerDirs = new Vector3[6];
		for (int i = 0; i < n; i++)
		{
			Vector3 center = dirs[i];
			ulong cellId = _ball.CellIds[i];
			ulong[] vids = H3.CellToVertexes(cellId);      // 集合序（不保证环序；采样无序要求）
			int cornerCount = Math.Min(vids.Length, 6);    // 五边形格 5 角点 ⇒ 剩余权重按实际角数均分
			float cornerW = (1f - CenterWeight) / cornerCount;
			for (int k = 0; k < cornerCount; k++)
			{
				int vi = _ball.VertexIndexOf(vids[k]);
				cornerDirs[k] = _ball.VertexPositions[vi].Normalized();
			}
			float sum = field.Sample(center) * CenterWeight;
			for (int k = 0; k < cornerCount; k++) sum += field.Sample(cornerDirs[k]) * cornerW;
			elev[i] = sum;
		}
		return elev;
	}
}
