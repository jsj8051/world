using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace World.Params;

/// <summary>
/// **参数文件的宿主 I/O 层**（`World.Params`，`scripts/Params/`）：只管"文件在哪、能不能读、能不能写"；
/// 文本怎么变成实例、实例归谁、怎么改参数归 <see cref="WorldParams"/>。
/// 两个档都在项目根 `res/params/`：<see cref="PresetRelativePath"/>（正库）与 <see cref="UserRelativePath"/>（可写、整份覆盖默认档、删掉 = 恢复出厂）。
/// 内容根 = 含 `res/params/` 的那一层；磁盘分支命中时零引擎调用，只有导出产物才走 `Godot.FileAccess`，
/// 而那时 `res://` 只读 ⇒ 写失败作为问题返回。
/// </summary>
public static class WorldParamStore
{
	/// <summary>**默认档**（正库数据文件，随游戏走）：项目根 `res/params/` 下的 `world_params.json`。</summary>
	public const string PresetRelativePath = "res/params/world_params.json";

	/// <summary>**用户档**（生效档 / 可写）：与默认档同目录的 `world_params.user.json`（整份覆盖默认档）。</summary>
	public const string UserRelativePath = "res/params/world_params.user.json";

	/// <summary>参数目录（`res/` 下唯一放参数 JSON 的地方）。</summary>
	public const string ParamsDirRelativePath = "res/params";

	/// <summary>`res://` 下的默认档（磁盘分支未命中时——即导出产物里——的唯一读法）。</summary>
	const string PresetResPath = "res://" + PresetRelativePath;

	/// <summary>`res://` 下的用户档（写入落点）。</summary>
	const string UserResPath = "res://" + UserRelativePath;

	/// <summary>
	/// **内容根解析上界**（从程序集目录向上找的层数）。**显式常量、带守卫**：
	/// 历史教训是 `i &lt; 10` 这种隐式契约（目录再搬深就静默饿死），故这里给它名字。
	/// 实测各宿主深度（到含 `res/params/` 的仓库根）：编辑器/headless 5 层、单测 5 层 ⇒ 12 有足够余量。
	/// </summary>
	const int ContentRootMaxUpLevels = 12;

	// ─────────────────────────────────────────────────────────────────────
	// 读：默认档 / 用户档（两条口，都由参数管理器调用）
	// ─────────────────────────────────────────────────────────────────────

	/// <summary>
	/// **读默认档文本**（正库"地球档"）。读不到 ⇒ 记问题并返回 `""`
	/// （管理器据此判定"该用出厂档实例值"，而不是把世界变成全 0）。
	/// </summary>
	public static string ReadPresetText(out IReadOnlyList<string> problems)
	{
		var list = new List<string>();
		string text = ReadRelative(PresetRelativePath, PresetResPath, "默认档", list);
		if (text.Length == 0)
			list.Add($"默认档不可用（`{PresetRelativePath}` 磁盘读不到、`{PresetResPath}` 也读不到）" +
					 "⇒ 本次世界定义使用**出厂档实例值**（spec 属性初值），未从文件读入。");
		problems = list;
		return text;
	}

	/// <summary>
	/// **读用户档文本**（可写的那份）。**文件不存在是正常状态**（等价"没有任何用户改动"）
	/// ⇒ 不记问题、返回 `""`；读失败 / 路径不可解析才记问题。
	/// </summary>
	public static string ReadUserText(out IReadOnlyList<string> problems)
	{
		var list = new List<string>();
		string text = ReadRelative(UserRelativePath, UserResPath, "用户档", list, missingIsNormal: true);
		problems = list;
		return text;
	}

	/// <summary>
	/// **写用户档文本**（参数管理器"改参数"落盘的那一半）。
	/// 成功 ⇒ 留一条落盘提示；失败（含导出产物里 `res://` 只读）⇒ 记问题并返回 `false`——**不静默**。
	/// </summary>
	public static bool WriteUserText(string text, out IReadOnlyList<string> problems)
	{
		var list = new List<string>();
		if (string.IsNullOrEmpty(text))
		{
			list.Add("用户档文本为空 ⇒ 拒绝写盘（避免把参数文件清成空文件）。");
			problems = list;
			return false;
		}

		// ① 磁盘优先（编辑器 / headless / 单测都能落盘，且不碰引擎）
		string diskPath = TryResolveDiskPath(UserRelativePath);
		if (diskPath != null && WriteDiskText(diskPath, text, "用户档", list))
		{
			problems = list;
			return true;
		}

		// ② 引擎分支（导出产物：磁盘上没有 res/ 目录，只能靠 Godot FileAccess 写——但那通常是只读的）
		if (TryWriteResText(UserResPath, text, list))
		{
			problems = list;
			return true;
		}

		list.Add($"用户档写盘失败（`{UserRelativePath}`）：参数已在**内存实例**里改好，" +
				 "但没能落到文件——导出产物里 `res://` 是只读的（编辑器 / headless 下可写）。");
		problems = list;
		return false;
	}

	/// <summary>参数目录是否可在磁盘上定位（诊断 / 契约用：证明 `res/params/` 真的存在）。</summary>
	public static string ResolveParamsDir() => TryResolveDiskPath(ParamsDirRelativePath);

	/// <summary>默认档的磁盘绝对路径（解析不到 ⇒ `null`）。★路径拼装留在本层，管理器不碰 `System.IO`。</summary>
	public static string ResolvePresetPath() => TryResolveDiskPath(PresetRelativePath);

	/// <summary>用户档的磁盘绝对路径（解析不到 ⇒ `null`）。</summary>
	public static string ResolveUserPath() => TryResolveDiskPath(UserRelativePath);

	// ─────────────────────────────────────────────────────────────────────
	// 路径解析
	// ─────────────────────────────────────────────────────────────────────

	/// <summary>把**相对内容根**的路径解析成磁盘绝对路径；解析不到（导出产物 / 目录搬太深）⇒ `null`。</summary>
	static string TryResolveDiskPath(string relativePath)
	{
		string root = FindContentRoot();
		if (root == null) return null;
		return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
	}

	/// <summary>
	/// **内容根** = 含 `res/params/` 那一层目录（= 项目根 = `res://` 在磁盘上的落点）。
	/// 从 <see cref="AppContext.BaseDirectory"/> 向上找，上界见 <see cref="ContentRootMaxUpLevels"/>。
	/// 找不到 ⇒ 返回 `null`（**那一刻不记问题**：那是"内容在 `.pck` 里"的正常信号，由调用方走引擎分支）。
	/// </summary>
	static string FindContentRoot()
	{
		string marker = ParamsDirRelativePath.Replace('/', Path.DirectorySeparatorChar);
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		for (int i = 0; i <= ContentRootMaxUpLevels && dir != null; i++)
		{
			if (Directory.Exists(Path.Combine(dir.FullName, marker))) return dir.FullName;
			dir = dir.Parent;
		}
		return null;
	}

	// ─────────────────────────────────────────────────────────────────────
	// 读 / 写：磁盘优先，引擎兜底（引擎面**全类唯一**）
	// ─────────────────────────────────────────────────────────────────────

	/// <summary>读一份参数文本：磁盘（内容根）优先；磁盘没有 ⇒ 引擎（`res://`，能读 `.pck` 内的内容）。</summary>
	/// <param name="missingIsNormal">文件不存在时是否算正常（用户档 = 正常，默认档 = 有问题）。</param>
	static string ReadRelative(string relativePath, string resPath, string what, List<string> list,
		bool missingIsNormal = false)
	{
		string diskPath = TryResolveDiskPath(relativePath);
		if (diskPath != null)
		{
			if (File.Exists(diskPath)) return ReadDiskText(diskPath, what, list);
			if (missingIsNormal) return "";
			list.Add($"{what}在磁盘上不存在（{diskPath}）⇒ 试 `{resPath}`。");
		}

		string res = TryReadResText(resPath, list);
		if (res != null) return res;

		if (!missingIsNormal)
			list.Add($"{what}不可用：磁盘找不到内容根（含 `{ParamsDirRelativePath}` 的那一层），" +
					 $"`{resPath}` 也读不到 ⇒ {what}缺失。");
		return "";
	}

	/// <summary>读磁盘文件：文件不存在 ⇒ `""`（不记问题）；读失败 ⇒ 记问题并返回 `""`。</summary>
	static string ReadDiskText(string path, string what, List<string> list)
	{
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

	/// <summary>写磁盘文件（目录不存在则建）。成功留一条落盘提示（"参数改了、文件也改了"必须可见）。</summary>
	static bool WriteDiskText(string path, string text, string what, List<string> list)
	{
		try
		{
			string dir = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
			File.WriteAllText(path, text);
			list.Add($"已写入{what} → {path}");
			return true;
		}
		catch (Exception e)
		{
			list.Add($"写{what}失败（{path}）：{e.Message}");
			return false;
		}
	}

	// ─────────────────────────────────────────────────────────────────────
	// 引擎逃生门——**全类唯一**碰 Godot API 的地方
	//
	//  · 只在"磁盘分支没命中"时被调用；
	//  · `NoInlining` + `try/catch` 与 `World.Utils.H3.H3Native` 同型：非 Godot 宿主下这些调用
	//    会抛（而不是崩溃），守住即可——万一磁盘分支意外落空，也只是"多一条问题消息"。
	// ─────────────────────────────────────────────────────────────────────

	/// <summary>用 Godot `FileAccess` 读 `res://` 文本（导出后内容在 `.pck` 内，`System.IO` 读不到）。</summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	static string TryReadResText(string resPath, List<string> list)
	{
		try
		{
			if (!Godot.FileAccess.FileExists(resPath)) return null;
			using var f = Godot.FileAccess.Open(resPath, Godot.FileAccess.ModeFlags.Read);
			return f?.GetAsText();
		}
		catch (Exception e)
		{
			list.Add($"经 Godot FileAccess 读 {resPath} 失败：{e.Message}");
			return null;
		}
	}

	/// <summary>用 Godot `FileAccess` 写 `res://` 文本。导出产物里通常只读 ⇒ 返回 `false` 并记问题。</summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	static bool TryWriteResText(string resPath, string text, List<string> list)
	{
		try
		{
			using var f = Godot.FileAccess.Open(resPath, Godot.FileAccess.ModeFlags.Write);
			if (f == null)
			{
				list.Add($"经 Godot FileAccess 打开 {resPath} 失败：{Godot.FileAccess.GetOpenError()}");
				return false;
			}
			f.StoreString(text);
			return true;
		}
		catch (Exception e)
		{
			list.Add($"经 Godot FileAccess 写 {resPath} 失败：{e.Message}");
			return false;
		}
	}
}
