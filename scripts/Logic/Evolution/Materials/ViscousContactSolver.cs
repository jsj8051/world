using System;
using System.Collections.Generic;
using System.Threading;
using World.Data;

namespace World.Logic;

/// <summary>线性黏性格间力、远场阻力和单向法向接触的力平衡。</summary>
internal static class ViscousContactSolver
{
	internal readonly record struct Velocity(double X, double Y, double Z)
	{
		public static Velocity From(SpherePointD direction) => new(direction.X, direction.Y, direction.Z);
		public static Velocity operator +(Velocity a, Velocity b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
		public static Velocity operator -(Velocity a, Velocity b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
		public static Velocity operator *(Velocity a, double k) => new(a.X * k, a.Y * k, a.Z * k);
		public double Dot(Velocity b) => X * b.X + Y * b.Y + Z * b.Z;
		public Velocity TangentTo(Velocity radial) => this - radial * Dot(radial);
	}

	internal readonly record struct Node(Velocity Radial, double Anchor, Velocity DrivingForce = default);

	// B = -1 表示远场速度固定，修正量为零。接触连边只传法向压力。
	internal readonly record struct Link(int A, int B, double Resistance, Velocity Normal = default, double ClosingSpeed = 0)
	{
		public bool IsContact => Normal != default;
	}

	public static Velocity[] Solve(Node[] nodes, IReadOnlyList<Link> links, CancellationToken cancellation = default)
	{
		int count = nodes.Length;
		var corrections = new Velocity[count];
		var residual = new Velocity[count];
		var direction = new Velocity[count];
		var product = new Velocity[count];
		var inverseDiagonal = new double[count];
		var active = new bool[links.Count];
		for (int i = 0; i < links.Count; i++)
			active[i] = !links[i].IsContact || links[i].ClosingSpeed > 0;

		for (int iteration = 0; iteration < 32; iteration++)
		{
			cancellation.ThrowIfCancellationRequested();
			for (int i = 0; i < count; i++)
			{
				residual[i] = nodes[i].DrivingForce;
				inverseDiagonal[i] = nodes[i].Anchor;
			}
			for (int i = 0; i < links.Count; i++)
			{
				if (!active[i]) continue;
				var link = links[i];
				inverseDiagonal[link.A] += link.Resistance;
				if (link.B >= 0) inverseDiagonal[link.B] += link.Resistance;
				if (!link.IsContact) continue;
				Velocity force = link.Normal * (link.Resistance * link.ClosingSpeed);
				residual[link.A] -= force;
				residual[link.B] += force;
			}
			Multiply(corrections);
			double error = 0;
			for (int i = 0; i < count; i++)
			{
				residual[i] = residual[i].TangentTo(nodes[i].Radial);
				error += residual[i].Dot(residual[i]);
				residual[i] -= product[i];
				inverseDiagonal[i] = 1 / inverseDiagonal[i];
				direction[i] = residual[i] * inverseDiagonal[i];
			}
			double tolerance = Math.Max(1e-24, error * 1e-18);
			double rho = Dot(residual, direction);
			for (int step = 0; ; step++)
			{
				if (Dot(residual, residual) <= tolerance) break;
				if (step == 256)
					throw new EvolutionStepException(EvolutionProblemCode.InvalidGeometry, "大陆变形速度未收敛");
				if ((step & 15) == 0) cancellation.ThrowIfCancellationRequested();
				Multiply(direction);
				double scale = rho / Dot(direction, product);
				double nextRho = 0;
				for (int i = 0; i < count; i++)
				{
					corrections[i] += direction[i] * scale;
					residual[i] -= product[i] * scale;
					nextRho += residual[i].Dot(residual[i]) * inverseDiagonal[i];
				}
				double beta = nextRho / rho;
				for (int i = 0; i < count; i++)
					direction[i] = residual[i] * inverseDiagonal[i] + direction[i] * beta;
				rho = nextRho;
			}
			bool changed = false;
			for (int i = 0; i < links.Count; i++)
			{
				var link = links[i];
				if (!link.IsContact) continue;
				double closing = link.ClosingSpeed + (corrections[link.A] - corrections[link.B]).Dot(link.Normal);
				if (Math.Abs(closing) <= 1e-8 || active[i] == (closing > 0)) continue;
				active[i] = closing > 0;
				changed = true;
			}
			if (!changed) return corrections;
		}
		throw new EvolutionStepException(EvolutionProblemCode.InvalidGeometry, "大陆碰撞接触状态未收敛");

		void Multiply(Velocity[] values)
		{
			for (int i = 0; i < count; i++) product[i] = values[i] * nodes[i].Anchor;
			for (int i = 0; i < links.Count; i++)
			{
				if (!active[i]) continue;
				var link = links[i];
				Velocity difference = link.B < 0 ? values[link.A] : values[link.A] - values[link.B];
				Velocity force = link.IsContact
					? link.Normal * (difference.Dot(link.Normal) * link.Resistance)
					: difference * link.Resistance;
				product[link.A] += force;
				if (link.B >= 0) product[link.B] -= force;
			}
			for (int i = 0; i < count; i++) product[i] = product[i].TangentTo(nodes[i].Radial);
		}

		static double Dot(Velocity[] a, Velocity[] b)
		{
			double sum = 0;
			for (int i = 0; i < a.Length; i++) sum += a[i].Dot(b[i]);
			return sum;
		}
	}
}
