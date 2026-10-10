using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using World.Data;               // WorldSpec / LandSeaSpec / TerrainSpec（参数实例）

namespace World.Params;

/// <summary>
/// **参数管理器**（`World.Params`，`scripts/Params/`）：参数实例的唯一持有者、JSON 的唯一编解码处、
/// 改参数的唯一正门。**实例类**（不是静态、不进场景树）：由根管理器 new + `Reload()` 后注入世界管理器。
/// 加载顺序：出厂档实例值 → `world_params.json` → `world_params.user.json`（存在则整份覆盖）。
/// 不合并字段、不逐字段校验；只有语法错 / 字段名拼错 / 类型不匹配报错（返回 null 且不动现值）。
/// ⚠️ 档里少写字段不报错（用 spec 属性初值）⇒ 给 spec 加字段时必须同步改两个档。
/// </summary>
public class WorldParams
{
	/// <summary>
	/// **当前生效的世界定义实例**（本管理器持有，恒非空、段恒非空）。
	/// ★**读参数就读它**；要**写**请走 <see cref="Set"/> / <see cref="ResetToFactory"/>
	///   （它们负责把改动同步到文件；直接改字段会得到"内存改了、文件没改"的半套状态）。
	/// </summary>
	public WorldSpec Active { get; } = new();

	/// <summary>上一次<b>加载</b>（默认档 / 用户档读取与装配）留下的问题清单；空 = 干净。</summary>
	public IReadOnlyList<string> LoadProblems { get; private set; } = Array.Empty<string>();

	/// <summary>上一次<b>写盘</b>（<see cref="Save"/>）留下的问题清单；空 = 落盘成功。</summary>
	public IReadOnlyList<string> SaveProblems { get; private set; } = Array.Empty<string>();

	/// <summary>
	/// 上一次<b>改参数</b>（<see cref="Set"/>）的完整问题清单——含"参数名不认识 / 值解析不了"
	/// 这类**没走到写盘**的失败。空 = 实例与文件都改好了。
	/// </summary>
	public IReadOnlyList<string> LastProblems { get; private set; } = Array.Empty<string>();

	/// <summary>已经加载过一次了吗（构造后调一次 <see cref="Reload"/> 即为真）。</summary>
	public bool IsLoaded { get; private set; }

	// ─────────────────────────────────────────────────────────────────────
	// JSON ⇄ WorldSpec（唯一的转换处）
	//
	// ★这三个口是**静态纯函数**（文本 ⇄ 实例，不碰本实例的状态），
	//   而 `Active` / `Reload` / `Set` / `Save` 是**实例**口（持有并改写那份世界定义）。
	//   两类口职责不同，故不做统一；新增方法时按"要不要碰 Active"来选。
	//   文件路径**不在这里**：那是 I/O 的事，用 `WorldParamStore.ResolvePresetPath()` 等。
	// ─────────────────────────────────────────────────────────────────────

	/// <summary>
	/// 读写选项：缩进、中文不转义、允许注释与尾逗号、**字段名拼错即报错**。
	/// ★宽容度是**有意**的：参数档是给人手改的，`//` 说明与尾逗号必须能用；
	///   而字段名拼错必须报错（否则等于静默用出厂值——那是最坏的一类错）。
	/// </summary>
	/// <remarks>
	/// ★编码器用 <see cref="JavaScriptEncoder.Create"/> 而不是 `UnsafeRelaxedJsonEscaping`：保留 `<` `>` `&`
	///   等 HTML 敏感字符的转义。注意它**仍会转义反引号**（`` ` `` → `\u0060`），故档里的注释用全角「」。
	/// ★**精度纪律**：按属性类型读写 ⇒ `float` 字段读成 `float`（`0.29` 就是 `0.29`）。
	/// </remarks>
	public static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
		Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
	};

	static bool IsSection(Type t) => t == typeof(LandSeaSpec) || t == typeof(TerrainSpec);

	/// <summary>
	/// **读成一份新实例**：`JSON 文本 → WorldSpec`（直接反序列化，不做字段补齐）。
	/// 坏档 ⇒ 记问题并返回 `null`，**绝不返回半份实例**。**不碰磁盘**。
	/// </summary>
	public static WorldSpec ReadJson(string json, out IReadOnlyList<string> problems)
	{
		var list = new List<string>();
		var spec = ReadJson(json, list);
		problems = list;
		return spec;
	}

	/// <summary>
	/// **读进既有实例**（`target` 的**实例身份不变**——"用参数就从实例用"能成立的前提）。
	/// 坏档 ⇒ 记问题、**逐字段一个也不改**。
	/// ★这是"反序列化只会造新对象"与"我们要就地更新同一个实例"之间的那一步适配。
	/// </summary>
	public static bool ReadJsonInto(string json, WorldSpec target, out IReadOnlyList<string> problems)
	{
		var list = new List<string>();
		var fresh = ReadJson(json, list);
		if (fresh == null)
		{
			problems = list;
			return false;
		}
		fresh.CopyTo(target, list);
		problems = list;
		return true;
	}

	/// <summary>**写出**：`WorldSpec` 实例 → JSON 文本（写用户档走这里）。失败 ⇒ 报问题并返回 `""`。</summary>
	public static string ToJson(WorldSpec spec, out IReadOnlyList<string> problems)
	{
		var list = new List<string>();
		string json = "";
		try
		{
			json = JsonSerializer.Serialize(spec, JsonOptions);
		}
		catch (Exception e)
		{
			list.Add($"世界定义无法序列化成 JSON：{e.Message}");
		}
		problems = list;
		return json;
	}

	static WorldSpec ReadJson(string json, List<string> list)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			list.Add("世界定义文本为空 ⇒ 无内容可装配。");
			return null;
		}

		try
		{
			var spec = JsonSerializer.Deserialize<WorldSpec>(json, JsonOptions);
			if (spec == null)
			{
				list.Add("世界定义的根节点不是对象（JSON 解析返回 null）。");
				return null;
			}
			// 段是 spec 的不变量（恒非空）：正常走不到这里（缺席的段自动落回出厂值）——
			// 留着是防"有人把段改成可空 / 去掉初值"。
			if (spec.LandSea == null) list.Add($"世界定义缺 {nameof(WorldSpec.LandSea)} 段（该段保持现值）。");
			if (spec.Terrain == null) list.Add($"世界定义缺 {nameof(WorldSpec.Terrain)} 段（该段保持现值）。");
			return spec;
		}
		catch (JsonException e)
		{
			list.Add($"世界定义无法装配成 WorldSpec：{e.Message}");
			return null;
		}
	}

	// ─────────────────────────────────────────────────────────────────────
	// 加载 / 保存
	// ─────────────────────────────────────────────────────────────────────

	/// <summary>
	/// **从文件重读生效的世界定义**：出厂档 → 默认档 → 用户档（**就地写入 <see cref="Active"/>**，
	/// 实例身份不变）。返回加载问题清单（同样可从 <see cref="LoadProblems"/> 取）。
	/// ★坏档**不会**把世界打成半份：坏的那一层被跳过（保留上一层值）并记问题。
	/// </summary>
	public IReadOnlyList<string> Reload()
	{
		var list = new List<string>();

		// ① 出厂档实例值 = 基底（spec 属性初值）。回出厂不用建新实例——逐字段拷进去，身份不变。
		new WorldSpec().CopyTo(Active, list);

		// ② 默认档（正库"地球档"）：读不到 ⇒ 保留出厂值，并记问题。
		//   ★`ReadJsonInto` 恒被调用（哪怕文本为空）——它自己会报"文本为空"，且 out 参数一定被赋值。
		string presetJson = WorldParamStore.ReadPresetText(out var presetProblems);
		list.AddRange(presetProblems);
		if (!ReadJsonInto(presetJson, Active, out var presetCodecProblems))
			list.Add("默认档装配失败 ⇒ 本段保留出厂档实例值（世界仍是可跑的地球档，不是全 0）。");
		list.AddRange(presetCodecProblems);

		// ③ 用户档：存在则**整份覆盖**；不存在是正常状态（等价"没有任何用户改动"）。
		string userJson = WorldParamStore.ReadUserText(out var userProblems);
		list.AddRange(userProblems);
		if (userJson.Length > 0)
		{
			if (!ReadJsonInto(userJson, Active, out var userCodecProblems))
				list.Add("用户档装配失败 ⇒ 保留默认档值（用户改动本次不生效）。");
			list.AddRange(userCodecProblems);
		}

		LoadProblems = list;
		IsLoaded = true;
		return list;
	}

	/// <summary>**把当前实例整份写回用户档**（<see cref="Set"/> 的第二步）。返回写盘问题清单（空 = 成功）。</summary>
	public IReadOnlyList<string> Save()
	{
		string json = ToJson(Active, out var codecProblems);
		var list = new List<string>(codecProblems);
		if (json.Length == 0)
		{
			SaveProblems = list;
			return list;
		}
		if (!WorldParamStore.WriteUserText(json, out var writeProblems)) list.AddRange(writeProblems);
		SaveProblems = list;
		return list;
	}

	/// <summary>**回出厂档**：把出厂档实例值就地拷回 <see cref="Active"/>（身份不变），并写回用户档。</summary>
	public IReadOnlyList<string> ResetToFactory()
	{
		var list = new List<string>();
		new WorldSpec().CopyTo(Active, list);
		list.AddRange(Save());
		return list;
	}

	// ─────────────────────────────────────────────────────────────────────
	// 改参数（UI 的正门）
	// ─────────────────────────────────────────────────────────────────────

	/// <summary>
	/// **改一个参数**：`name` 如 <c>"Seed"</c> / <c>"LandSea.LandFraction"</c> /
	/// <c>"Terrain.TargetRegionAreaKm2"</c>，`value` 用**字符串**写（UI 输入天然是文本）。
	///
	/// 步骤：① 解析路径 → ② 按目标属性类型解析值 → ③ **改实例** → ④ **整份写回用户档**。
	/// 返回本次问题清单（**空 = 实例与文件都改好了**）：
	/// · 路径不认识 / 值解析不了 ⇒ 报错，**实例与文件都不动**；
	/// · 实例改好了但**写盘失败**（如导出产物里 `res://` 只读）⇒ 报明"内存已改、文件没改"，**不回滚内存**。
	/// </summary>
	public IReadOnlyList<string> Set(string name, string value)
	{
		var list = new List<string>();

		if (!TryResolvePath(name, out var target, out var property, out var pathProblem))
		{
			list.Add(pathProblem);
			list.Add("可用参数：" + string.Join("、", ParameterNames()));
			LastProblems = list;
			return list;
		}

		if (!TryParseValue(property, value, out object parsed, out var valueProblem))
		{
			list.Add($"参数 `{name}` {valueProblem}");
			LastProblems = list;
			return list;
		}

		property.SetValue(target, parsed);   // ③ 改实例（就地：持引用者立刻看到新值）
		SaveProblems = Save();               // ④ 整份写回用户档
		list.AddRange(SaveProblems);
		LastProblems = list;
		return list;
	}

	/// <summary>可用参数名（UI 建面板 / 报错列清单都从这里取——**不由 UI 自己维护一份**）。</summary>
	public IEnumerable<string> ParameterNames()
	{
		foreach (var p in typeof(WorldSpec).GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			if (IsSection(p.PropertyType))
			{
				foreach (var sub in p.PropertyType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
					yield return $"{p.Name}.{sub.Name}";
			}
			else yield return p.Name;
		}
	}

	// ─────────────────────────────────────────────────────────────────────
	// 反射：路径 → 属性（**key 清单 = spec 类型自身**，不建注册表）
	// ─────────────────────────────────────────────────────────────────────

	/// <summary>
	/// 把 <c>"LandSea.LandFraction"</c> 解析成"持有者实例 + 要写的属性"（**针对本实例的 `Active`**——
	/// 每个 `WorldParams` 实例各管自己那份世界定义，不共享静态状态）。
	/// 只走**公开实例属性**（= spec 类型自身就是参数清单），不认下标、不认方法。
	/// </summary>
	bool TryResolvePath(string name, out object target, out PropertyInfo property, out string problem)
	{
		target = null;
		property = null;
		problem = null;          // 成功时无问题（out 参数必须在每条路径上都有值）

		if (string.IsNullOrWhiteSpace(name))
		{
			problem = "参数名是空的。";
			return false;
		}

		var segments = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
		object current = Active;
		for (int i = 0; i < segments.Length; i++)
		{
			string segment = segments[i].Trim();
			var p = current.GetType().GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
			if (p == null)
			{
				problem = $"没有参数 `{name}`（在 `{current.GetType().Name}` 上找不到 `{segment}`）。";
				return false;
			}

			bool last = i == segments.Length - 1;
			if (last)
			{
				if (IsSection(p.PropertyType))
				{
					problem = $"`{name}` 是一个段（{p.PropertyType.Name}），不是可改的单个参数。";
					return false;
				}
				target = current;
				property = p;
				return true;
			}

			current = p.GetValue(current);
			if (current == null)
			{
				problem = $"参数 `{name}` 的中间段 `{segment}` 是 null（世界定义不变量被破坏）。";
				return false;
			}
		}

		problem = $"没有参数 `{name}`。";
		return false;
	}

	/// <summary>
	/// 按**目标属性的类型**解析文本值（UI 输入是文本，落点类型由 spec 决定）：
	/// <c>int</c> / <c>float</c> / <c>double</c> / <c>bool</c> / <c>string</c>。
	/// </summary>
	static bool TryParseValue(PropertyInfo property, string value, out object parsed, out string problem)
	{
		parsed = null;
		problem = null;
		Type t = property.PropertyType;
		value = value?.Trim();

		if (t == typeof(int))
		{
			if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) { parsed = i; return true; }
			problem = $"需要整数，实得 `{value}`。";
			return false;
		}
		if (t == typeof(float))
		{
			if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) { parsed = f; return true; }
			problem = $"需要小数，实得 `{value}`。";
			return false;
		}
		if (t == typeof(double))
		{
			if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) { parsed = d; return true; }
			problem = $"需要小数，实得 `{value}`。";
			return false;
		}
		if (t == typeof(bool))
		{
			if (bool.TryParse(value, out bool b)) { parsed = b; return true; }
			problem = $"需要 true / false，实得 `{value}`。";
			return false;
		}
		if (t == typeof(string)) { parsed = value; return true; }

		problem = $"类型 `{t.Name}` 暂无文本解析口径（参数类型只准 int / float / double / bool / string）。";
		return false;
	}
}
