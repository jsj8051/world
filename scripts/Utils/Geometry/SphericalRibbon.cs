using System;
using System.Collections.Generic;
using Godot;

namespace World.Utils;

/// <summary>
/// 球面 ribbon 的通用几何工具：采样点处的切平面横向向量，以及"每点两顶点"三角带的索引生成。
/// 只做球面几何与索引排布，不含河流/洋流等世界语义；河流样条与流线粒子共用。
/// </summary>
public static class SphericalRibbon
{
	/// <summary>
	/// 采样点 i 处的横向单位向量（切线 × 径向，投影到点自身切平面）。
	/// 相邻采样点重合时无法定向：<paramref name="degenerate"/> = true，返回零向量，由调用方给自己的回退。
	/// </summary>
	public static Vector3 SideAt(Vector3[] pts, int i, out bool degenerate)
	{
		int n = pts.Length;
		var p = pts[i];
		var prev = pts[i > 0 ? i - 1 : 0];
		var next = pts[i + 1 < n ? i + 1 : n - 1];
		var v = next - prev;
		v -= p * p.Dot(v);                       // 投影到 p 的切平面
		float len = v.Length();
		if (len < 1e-9f)
		{
			degenerate = true;
			return Vector3.Zero;
		}
		degenerate = false;
		return p.Cross(v / len).Normalized();
	}

	/// <summary>
	/// 把一串"每个采样点两个顶点（上下成对）"接成三角带：相邻两对组成一个四边形（2 个三角形）。
	/// 每个四边形沿 inner→outer 对角切分，索引顺序固定，调用方只需给出起始顶点下标。
	/// 顶点布局约定：<c>2i</c> = 第 i 点外侧、<c>2i+1</c> = 第 i 点内侧。
	/// </summary>
	public static void AppendQuadStrip(IList<int> indices, int baseVertex, int pairCount)
	{
		for (int i = 0; i + 1 < pairCount; i++)
		{
			int a = baseVertex + 2 * i;
			indices.Add(a); indices.Add(a + 1); indices.Add(a + 2);
			indices.Add(a + 1); indices.Add(a + 3); indices.Add(a + 2);
		}
	}

	/// <summary>同上，写进预分配的索引数组（大网格走这条，避免 List 再拷一次）；返回写完后的游标。</summary>
	public static int AppendQuadStrip(int[] indices, int writeIndex, int baseVertex, int pairCount)
	{
		for (int i = 0; i + 1 < pairCount; i++)
		{
			int a = baseVertex + 2 * i;
			indices[writeIndex++] = a;
			indices[writeIndex++] = a + 1;
			indices[writeIndex++] = a + 2;
			indices[writeIndex++] = a + 1;
			indices[writeIndex++] = a + 3;
			indices[writeIndex++] = a + 2;
		}
		return writeIndex;
	}
}
