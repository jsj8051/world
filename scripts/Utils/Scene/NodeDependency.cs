using System.Collections.Generic;
using Godot;                 // Node / NodePath —— **仅类型与结构**，零 GD.* 调用（见下）

namespace World.Utils;

/// <summary>
/// **场景依赖解析**（装配层共用的两条口）：把"`[Export] NodePath` → 已解析且类型正确的节点"
/// 这件事收成一处，供各管理器复用。
///
/// ★为什么归 `World.Utils`（2026-10-11 用户拍板"util 也放开吧，可以放 godot 依赖的工具"）：
///   原先四个管理器**各抄一份**同型代码（`WorldRoot`/`WorldManager`/`RenderManager`/`UIManager`
///   各一个 `Require`，约 90 行），且已出现文案漂移。工具层收编后只有一份判据。
///
/// ★为什么依赖必须是 `NodePath`（**实测结论**，别改成直接引用——见
///   `docs/债务清单-架构与分层.md` 的 A-6c）：本项目的跨层依赖全都指向**另一个 packed scene 的根**
///   （`WorldGenPlanet.tscn` / `OrbitalCamera.tscn` / `WorldView.tscn` / 两个面板场景），
///   而 Godot 的直接节点引用导出**不跨场景边界绑定**（实测：改成直接引用后启动即 null 抛异常）。
///   代价就是本类要照出的两类运行时错误：解析不到、类型不符。
///
/// ★**边界（放开 Utils 用 Godot 之后唯一要紧的那条）**：
///   本类只做**结构性**判断（`NodePath.IsEmpty` / `GetNodeOrNull` / `GetType`），
///   **不得**出现 `GD.*`（打印 / 报错 / 推送警告）——那是**运行期引擎调用**，
///   在无 Godot 宿主的单测进程里是**进程级崩溃**。诊断归调用方（装配层负责"照出来"）。
/// </summary>
public static class NodeDependency
{
	/// <summary>
	/// **取一个必需依赖**：路径空 / 解析不到 / 类型不符 ⇒ 抛（启动即失败，别留到运行中途）。
	/// <para>用法（调用点是扩展方法，无需改签名）：`Planet = Require&lt;WorldGenPlanet&gt;(PlanetPath, nameof(PlanetPath));`</para>
	/// <para>★异常前缀自动取**宿主类型名**（`WorldRoot` / `WorldManager` / …）⇒ 不再靠每个管理器
	/// 手写 `[XXXMANAGER]` 标签（原先四处各写一遍，必然漂移）。</para>
	/// </summary>
	/// <param name="host">解析起点（调用它的那个管理器节点；扩展方法的 <c>this</c>）。</param>
	/// <param name="path">在检视面板里指向目标节点的导出路径。</param>
	/// <param name="label">报错时显示的名字（约定传 <c>nameof(那个属性)</c>）。</param>
	public static T Require<T>(this Node host, NodePath path, string label) where T : Node
	{
		var who = host.GetType().Name;

		if (path == null || path.IsEmpty)
			throw new System.InvalidOperationException($"[{who}] 依赖未配置：{label}（在检视面板里指向对应节点）");

		var node = host.GetNodeOrNull(path);
		if (node == null)
			throw new System.InvalidOperationException($"[{who}] 依赖解析失败：{label} → `{path}`（场景里没有这个节点？）");

		if (node is not T typed)
			throw new System.InvalidOperationException(
				$"[{who}] 依赖类型不符：{label} → `{path}` 是 {node.GetType().Name}，期望 {typeof(T).Name}");

		return typed;
	}

	/// <summary>
	/// **编辑器侧依赖校验**（与 <see cref="Require{T}"/> 同判据，只是不抛而是攒成警告）——
	/// 供管理器在 `_GetConfigurationWarnings()` 里汇总，做到"打开根节点就能看全整棵树的接线问题"。
	/// </summary>
	/// <param name="host">校验起点（调用它的那个节点）。</param>
	/// <param name="path">导出路径。</param>
	/// <param name="label">显示名（约定 <c>nameof(属性)</c>）。</param>
	/// <param name="warnings">累加目标（调用方自己的列表）。</param>
	public static void Check<T>(this Node host, NodePath path, string label, List<string> warnings)
		where T : Node
	{
		if (warnings == null) return;

		if (path == null || path.IsEmpty) { warnings.Add($"{label} 未配置"); return; }

		var node = host.GetNodeOrNull(path);
		if (node == null) { warnings.Add($"{label} → `{path}` 找不到"); return; }
		if (node is not T) warnings.Add($"{label} → `{path}` 类型应为 {typeof(T).Name}");
	}
}
