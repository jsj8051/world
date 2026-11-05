using System;
using Godot;

namespace World.Logic;

/// <summary>三份风视图的身份：地面 / 低空 / 自由。同一个 p、同一个梯度，只有摩擦闭合不同。</summary>
public enum WindKind
{
	/// <summary>地面风：出图、风丝、风反转验证。地形减速只乘这一套。</summary>
	Surface = 0,

	/// <summary>低空风（摩擦较小）：水汽输送、地形雨。</summary>
	Low = 1,

	/// <summary>自由低层风（摩擦最小）：洋流的风应力。</summary>
	Free = 2,
}

/// <summary>
/// 一份风事实（某个摩擦闭合）：逐月矢量 + 年合成矢量 + 年均标量风速。
/// 逐月显示风速 = 矢量长度（派生，不存——两套值同源靠约定是 bug 的温床）；
/// 年均只能存标量均值：`|mean v| ≠ mean |v|`，季风区年合成矢量会互相抵消。
///
/// @example
/// <code>
/// var july = facts.Wind.Surface.Monthly[6][i];
/// float mapSpeed = facts.Wind.Surface.SpeedAt(6, i);
/// </code>
/// </summary>
public sealed class WindSet
{
	/// <summary>这一份视图的身份。</summary>
	public WindKind Kind { get; }

	/// <summary>逐月风场（<c>[month][cell]</c>；切向向量，长度 = 风速 0–1）。</summary>
	public Vector3[][] Monthly { get; }

	/// <summary>年合成风（12 个月矢量均值）。</summary>
	public Vector3[] Annual { get; }

	/// <summary>年均显示风速（12 个月 <c>|v|</c> 的算术均值）。</summary>
	public float[] AnnualSpeed { get; }

	/// <summary>月数。</summary>
	public int MonthCount => Monthly.Length;

	internal WindSet(WindKind kind, Vector3[][] monthly, Vector3[] annual, float[] annualSpeed)
	{
		Kind = kind;
		Monthly = monthly ?? throw new ArgumentNullException(nameof(monthly));
		Annual = annual ?? throw new ArgumentNullException(nameof(annual));
		AnnualSpeed = annualSpeed ?? throw new ArgumentNullException(nameof(annualSpeed));
	}

	/// <summary>某月显示风速（0–1）；month ≤ 0 = 年均（标量均值）。</summary>
	public float SpeedAt(int month, int cell) =>
		month <= 0 ? AnnualSpeed[cell] : Monthly[month - 1][cell].Length();
}

/// <summary>
/// 三份风的只读事实（地面 / 低空 / 自由）：同一次月循环产出、共享同一个气压场与同一个梯度。
/// 三份是同一次求解的三个闭合，不是三个模型、也不是三层大气。
///
/// @example
/// <code>
/// var surface = facts.Wind.Get(WindKind.Surface);
/// var free = facts.Wind.Free;
/// </code>
/// </summary>
public sealed class WindField
{
	readonly WindSet[] _sets = new WindSet[3];

	/// <summary>由三份视图建立风事实；尺寸在构造时校验（月数 / 格数 / 逐月数组）。</summary>
	public WindField(WindSet surface, WindSet low, WindSet free)
	{
		if (surface == null) throw new ArgumentNullException(nameof(surface));
		if (low == null) throw new ArgumentNullException(nameof(low));
		if (free == null) throw new ArgumentNullException(nameof(free));
		if (surface.Kind != WindKind.Surface || low.Kind != WindKind.Low || free.Kind != WindKind.Free)
			throw new ArgumentException("风集身份不匹配：必须按 Surface / Low / Free 传入");

		int months = surface.MonthCount;
		int cells = surface.Annual.Length;
		if (months < 1) throw new ArgumentException("风场没有月份");
		Validate(surface, months, cells);
		Validate(low, months, cells);
		Validate(free, months, cells);

		_sets[(int)WindKind.Surface] = surface;
		_sets[(int)WindKind.Low] = low;
		_sets[(int)WindKind.Free] = free;
	}

	/// <summary>按身份取一份视图（地面 / 低空 / 自由）。</summary>
	public WindSet Get(WindKind kind) => _sets[(int)kind];

	/// <summary>地面风（出图 / 风丝 / 反转验证）。</summary>
	public WindSet Surface => _sets[(int)WindKind.Surface];

	/// <summary>低空风（水汽 / 地形雨）。</summary>
	public WindSet Low => _sets[(int)WindKind.Low];

	/// <summary>自由低层风（洋流）。</summary>
	public WindSet Free => _sets[(int)WindKind.Free];

	/// <summary>月数。</summary>
	public int MonthCount => Surface.MonthCount;

	static void Validate(WindSet set, int months, int cells)
	{
		if (set.MonthCount != months)
			throw new ArgumentException($"{set.Kind} 月数 {set.MonthCount} ≠ {months}");
		if (set.Annual.Length != cells || set.AnnualSpeed.Length != cells)
			throw new ArgumentException($"{set.Kind} 年均数组长度与格数 {cells} 不一致");
		for (int m = 0; m < months; m++)
			if (set.Monthly[m] == null || set.Monthly[m].Length != cells)
				throw new ArgumentException($"{set.Kind} 第 {m} 月数组长度与格数 {cells} 不一致");
	}
}
