using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using World.Data;               // WorldSpec / LandSeaSpec / TerrainSpec（纯数据形状）

namespace World.WorldGen;

/// <summary>
/// **世界参数管理器**——找到两份参数表、读进来、合成 <see cref="WorldSpec"/>。
/// 全系统"世界定义从哪来"的唯一答案。
///
/// ★三个文件层面的事实（2026-10-10 定型）：
///   · **默认档** = <see cref="PresetRelativePath"/>（正库数据文件，随游戏走，**只读**）——
///     "地球档"世界定义的**唯一**来源，代码里没有第二份；
///   · **玩家档** = <see cref="UserRelativePath"/>（游戏目录旁，不入库）——首跑由默认档**整份拷贝**
///     而来，之后只改它；它没写的字段仍由默认档补齐；
///   · **存储类** = spec 类型自身（`WorldSpec` / `LandSeaSpec` / `TerrainSpec` 的属性）——
///     不另建注册表、key 清单、字段类型表。
///   ⇒ 加一个世界参数只需改**两处**：spec 加字段 + 默认档给值；管理器与玩家档自动跟上。
///
/// ★**文件读写在本类**（用户拍板 2026-10-10）：场景层（`WorldGenPlanet` 等）完全不碰磁盘、
///   也不再有相关 `[Export]`。本类只用 <see cref="System.IO"/>——**不引任何引擎 API**，
///   故游戏（编辑器 / headless / 导出）、单测、`PerfBench` 共用同一份实现，路径由
///   <see cref="FindRoot"/> 按"向上找默认档"统一解析。
///
/// ★「只写想改的字段」与「老档兼容」由**合并**实现（默认档 ⊕ 玩家档，见 <see cref="Merge"/>）：
///   玩家档只写 <c>{"LandSea":{"LandFraction":0.5}}</c> 即可；spec 日后新增字段时，老玩家档缺的
///   那一项自动取默认档——**不会**被反序列化成 `0`（那是"看起来正常、世界静默改变"的最坏失败模式）。
///
/// ★坏数据不静默：JSON 语法错 / 根不是对象 / 字段名拼错 / 类型不匹配，一律记入 `problems` 并
///   **回落默认档**。字段名拼错的报错由 <see cref="JsonUnmappedMemberHandling.Disallow"/> 提供，
///   形如 "The JSON property 'NotAField' could not be mapped to any .NET member contained in type ..."。
///   **默认档坏掉是致命的**（无基底可落）⇒ 返回 `default(WorldSpec)` 并报问题，**故意不**偷偷用
///   某个内置默认值——那等于把默认值又搬回代码里。
///
/// ★精度纪律：**只在字段类型的精度上做事**。`System.Text.Json` 按属性类型读写——`float` 字段读成
///   `float`、写出（拷贝默认档原文）保持 float 最短往返格式（`0.29` 就是 `0.29`）。
///
/// ★JSON 形状（= spec 结构）：
/// <code>
/// { "Seed": 42, "LandSea": { "ContinentCount": 7, ... }, "Terrain": { "TargetRegionAreaKm2": 4000000 } }
/// </code>
/// 支持 `//`、`/* */` 注释与尾逗号，故可在文件里自由写说明。
/// </summary>
public static class WorldParamTable
{
	/// <summary>默认档相对位置（正库数据文件，随游戏走）。</summary>
	public const string PresetRelativePath = "data/world_params.json";

	/// <summary>玩家档相对"内容根"的位置（随 `userdata/` 一起不入库）。</summary>
	public const string UserRelativePath = "userdata/params/world_params.json";

	/// <summary>
	/// 读写选项：缩进、中文不转义、允许注释与尾逗号、**字段名拼错即报错**。
	/// </summary>
	/// <remarks>
	/// ★编码器用 <see cref="JavaScriptEncoder.Create"/> 而不是 `UnsafeRelaxedJsonEscaping`：保留 `<` `>` `&`
	///   等 HTML 敏感字符的转义。注意它**仍会转义反引号**（`` ` `` → `\u0060`，`UnicodeRanges.All` 解不开），
	///   故档里的注释不要用反引号——用全角「」。
	/// </remarks>
	static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
	};

	/// <summary><see cref="JsonNode"/> 解析选项（与 <see cref="Options"/> 同样的宽容度）。</summary>
	static readonly JsonDocumentOptions NodeOptions = new()
	{
		CommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	// ─────────────────────────────────────────────────────────────────────
	// 对外三个口
	// ─────────────────────────────────────────────────────────────────────

	/// <summary>
	/// **只读默认档**（"地球档"）——不碰玩家档、不写任何文件。
	/// 用途：测试 / 基准取一个稳定的世界定义；将来做"恢复出厂设置"也用这个。
	/// </summary>
	public static WorldSpec Preset(out IReadOnlyList<string> problems)
	{
		var list = new List<string>();
		string root = FindRoot(list);
		string json = root == null ? "" : ReadText(Path.Combine(root, PresetRelativePath), "默认档", list);

		var spec = Merge("", json, list);
		problems = list;
		return spec;
	}

	/// <summary>
	/// **读入生效的世界定义** = 默认档 ⊕ 玩家档。玩家档缺失时从默认档**整份拷贝**一份（保留其注释）。
	/// <paramref name="problems"/> = 需要调用方打出来的消息（含"已拷出玩家档"这类提示与所有坏数据报告）。
	/// </summary>
	public static WorldSpec Load(out IReadOnlyList<string> problems)
	{
		var list = new List<string>();

		string root = FindRoot(list);
		if (root == null) { problems = list; return default; }

		string presetJson = ReadText(Path.Combine(root, PresetRelativePath), "默认档", list);

		// 玩家档缺失 ⇒ 拷一份默认档（首次运行即得到一份可编辑的完整表；删掉它 = 恢复出厂设置）
		string userPath = Path.Combine(root, UserRelativePath);
		if (!File.Exists(userPath))
		{
			try
			{
				string dir = Path.GetDirectoryName(userPath);
				if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
				File.WriteAllText(userPath, presetJson);
				list.Add($"已从默认档拷出玩家参数表 → {userPath}");
			}
			catch (Exception e)
			{
				list.Add($"写玩家参数表失败（{userPath}）：{e.Message}");
			}
		}

		string userJson = ReadText(userPath, "玩家档", list);
		var spec = Merge(userJson, presetJson, list);
		problems = list;
		return spec;
	}

	/// <summary>
	/// **纯函数内核**：两份 JSON 文本 → `WorldSpec`。<paramref name="userJson"/> 可只写要改的字段，
	/// 其余取 <paramref name="presetJson"/>。<see cref="Load"/> 与 <see cref="Preset"/> 都是它的薄壳
	/// （分别喂"默认档＋玩家档"与"默认档＋空"），测试也直接调它——**不碰磁盘**。
	/// </summary>
	public static WorldSpec Merge(string userJson, string presetJson, out IReadOnlyList<string> problems)
	{
		var list = new List<string>();
		var spec = Merge(userJson, presetJson, list);
		problems = list;
		return spec;
	}

	// ─────────────────────────────────────────────────────────────────────
	// 内部
	// ─────────────────────────────────────────────────────────────────────

	/// <summary>
	/// 合并内核（问题写进调用方的 <paramref name="list"/>，好让 <see cref="Load"/> 把"找路径 / 拷贝"
	/// 与本步的消息汇总成一份有序清单）。默认档不可用 ⇒ 返回 `default(WorldSpec)`。
	/// </summary>
	static WorldSpec Merge(string userJson, string presetJson, List<string> list)
	{
		var preset = ParseObject(presetJson, "默认档", list);
		if (preset == null) return default;

		var presetSpec = Deserialize(preset, "默认档", list);
		if (presetSpec == null) return default;

		// 空玩家档 = "没有任何覆盖"，是**正常状态**（等价于照抄默认档），不是坏数据 ⇒ 不报问题。
		if (string.IsNullOrWhiteSpace(userJson)) return presetSpec.Value;

		var patch = ParseObject(userJson, "玩家档", list);
		if (patch == null)
		{
			list.Add("玩家参数表不可用 ⇒ 全部使用默认档。");
			return presetSpec.Value;
		}

		var merged = preset.DeepClone().AsObject();
		Overlay(merged, patch);

		var spec = Deserialize(merged, "玩家档", list);
		if (spec == null)
		{
			list.Add("玩家参数表装配失败 ⇒ 全部使用默认档。");
			return presetSpec.Value;
		}
		return spec.Value;
	}

	/// <summary>
	/// **内容根** = 含默认档的那一层目录。从 <see cref="AppContext.BaseDirectory"/> 向上找（≤10 层）：
	///   · 编辑器 / headless：程序集在 `.godot/mono/temp/bin/**` ⇒ 找到**项目根**（= `res://` 的磁盘路径）；
	///   · 单测 / 基准：程序集在 `bin/Debug/net8.0` ⇒ 找到**仓库根**；
	///   · 导出：找到**可执行文件目录**（前提是导出流程把 `data/` 放在 exe 旁）。
	/// 只用 <see cref="System.IO"/> ⇒ 不需要引擎宿主，"游戏 / 测试 / 基准"三处共用同一套解析。
	/// </summary>
	static string FindRoot(List<string> list)
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		for (int i = 0; i < 10 && dir != null; i++)
		{
			if (File.Exists(Path.Combine(dir.FullName, PresetRelativePath))) return dir.FullName;
			dir = dir.Parent;
		}
		list.Add($"向上 10 层仍未找到默认档 {PresetRelativePath} ⇒ 世界定义不可用（仓库 / 导出结构变了？）");
		return null;
	}

	/// <summary>读文本：文件不存在 ⇒ 返回 `""`（是否算问题由调用方判定）；读失败 ⇒ 记问题并返回 `""`。</summary>
	static string ReadText(string path, string what, List<string> list)
	{
		if (!File.Exists(path)) return "";
		try
		{
			return File.ReadAllText(path);
		}
		catch (Exception e)
		{
			list.Add($"读{what}失败（{path}）：{e.Message}");
			return "";
		}
	}

	/// <summary>解析成对象；失败 / 不是对象 ⇒ 记问题并返回 null。</summary>
	static JsonObject ParseObject(string json, string what, List<string> list)
	{
		try
		{
			if (JsonNode.Parse(json, documentOptions: NodeOptions) is JsonObject obj) return obj;
			list.Add($"{what}的根节点不是对象。");
			return null;
		}
		catch (JsonException e)
		{
			list.Add($"{what} JSON 解析失败：{e.Message}");
			return null;
		}
	}

	/// <summary>装配成 <see cref="WorldSpec"/>；失败 ⇒ 记问题并返回 null。</summary>
	static WorldSpec? Deserialize(JsonNode node, string what, List<string> list)
	{
		try
		{
			return node.Deserialize<WorldSpec>(Options);
		}
		catch (JsonException e)
		{
			list.Add($"{what}字段无法装配成 WorldSpec：{e.Message}");
			return null;
		}
	}

	/// <summary>
	/// 把 <paramref name="patch"/> 的字段逐层覆盖到 <paramref name="target"/>：对象**递归**、
	/// 其余（数字 / 字符串 / null）**整体替换**。只覆盖写了的字段 ⇒ 未写的保持默认档。
	/// </summary>
	static void Overlay(JsonObject target, JsonObject patch)
	{
		foreach (var (key, value) in patch)
		{
			if (value is JsonObject subPatch && target[key] is JsonObject subTarget) Overlay(subTarget, subPatch);
			else target[key] = value?.DeepClone();
		}
	}
}
