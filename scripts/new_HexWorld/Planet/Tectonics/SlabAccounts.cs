using System;
using Godot;

namespace World.NewHexWorld.Plate
{
	// 板片账户表（每板一份）：已俯冲板片的拉力账本。平流俯冲裁决时入账（质量面密度累计 +
	// 质量加权流向），每步按板片记忆衰减、按板内地壳总量封顶——物理意图：已俯冲的板片继续
	// 拉板块（真实地球的主驱动）。缝合划转 / 裂解分账 / 重启清零随板块生命周期走。
	// 纯账本：只管账户的存取与生命周期，力的换算（N × g × 面积、通道速度）留在 H3PlateMotion。
	// 2026-09-19 从 H3PlateMotion 拆出；对外口（AddSlab/TransferSlab/…）仍在 H3PlateMotion 上
	// 转发，调用方与测试不动。
	internal sealed class SlabAccounts
	{
		double[] _mass = Array.Empty<double>();
		Vector3[] _dir = Array.Empty<Vector3>();

		/// <summary>账户表长（越界读 = 0 / 零向量；H3PlateMotion 的共享表按它对齐）。</summary>
		public int TableLength => _mass.Length;

		/// <summary>账户质量（kg/m² 口径累计；越界板 = 0）。</summary>
		public double MassOf(int plate) => plate >= 0 && plate < _mass.Length ? _mass[plate] : 0.0;

		/// <summary>账户方向（质量加权流向和；越界板 = 零向量；恒有 |Dir| ≤ Mass）。</summary>
		public Vector3 DirOf(int plate) => plate >= 0 && plate < _dir.Length ? _dir[plate] : Vector3.Zero;

		/// <summary>按板片记忆衰减：fraction = StepMy / 记忆 My（调用方负责钳到 ≤ 1）。</summary>
		public void Decay(int plate, float fraction)
		{
			_mass[plate] *= 1.0 - fraction;
			_dir[plate] *= 1f - fraction;
		}

		/// <summary>入账：质量面密度 + 流向。方向归一后加权（调用方传的是质量加权流向和，
		/// 不能再乘一次质量 ⇒ |Dir| ≤ Mass）。capMass = 板内地壳总量 × 封顶比例（≤ 0 = 不封顶）。</summary>
		public void Add(int plate, double massPerArea, Vector3 flowDir, double capMass)
		{
			EnsureCapacity(plate);
			Vector3 unit = flowDir.LengthSquared() > 1e-18f ? flowDir.Normalized() : Vector3.Zero;
			_mass[plate] += massPerArea;
			_dir[plate] += unit * (float)massPerArea;
			if (capMass > 0 && _mass[plate] > capMass)
			{
				float scale = (float)(capMass / _mass[plate]);
				_mass[plate] = capMass;
				_dir[plate] *= scale;
			}
		}

		/// <summary>缝合划转：from 的账户按比例划给 to（物质归属改判 ⇒ 拉力随走）。</summary>
		public void Transfer(int from, int to, double fraction)
		{
			EnsureCapacity(Math.Max(from, to));
			fraction = Math.Clamp(fraction, 0.0, 1.0);
			double mass = _mass[from] * fraction;
			_mass[to] += mass;
			_dir[to] += _dir[from] * (float)fraction;
			_mass[from] -= mass;
			_dir[from] *= 1f - (float)fraction;
		}

		/// <summary>裂解分账：源板账户按份额分给新板（源板保留剩余份额）。</summary>
		public void Split(int source, int[] newPlates, double[] fractions)
		{
			foreach (int p in newPlates) EnsureCapacity(p);
			for (int k = 0; k < newPlates.Length; k++)
			{
				double mass = _mass[source] * fractions[k];
				_mass[newPlates[k]] += mass;
				_dir[newPlates[k]] += _dir[source] * (float)fractions[k];
				_mass[source] -= mass;
				_dir[source] *= 1f - (float)fractions[k];
			}
		}

		/// <summary>重启复位：全部清零（表长保留）。</summary>
		public void Clear()
		{
			Array.Clear(_mass, 0, _mass.Length);
			Array.Clear(_dir, 0, _dir.Length);
		}

		/// <summary>按板号扩容（只增不减；账户可能发生在 Step 之外，板号来自平流当步归属）。</summary>
		public void EnsureCapacity(int plate)
		{
			int required = plate + 1;
			if (_mass.Length >= required) return;
			int size = Math.Max(required, _mass.Length);
			Array.Resize(ref _mass, size);
			Array.Resize(ref _dir, size);
		}
	}
}
