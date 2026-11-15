using System;
using System.Collections.Generic;
using Godot;

namespace World.Utils;

/// <summary>
/// 按路径创建 shader 材质：加载 shader → new <see cref="ShaderMaterial"/> → 交给调用方设自己的 uniform。
///
/// 约定：
/// · **每个调用方一份实例**（不共享）——各图层的 uniform 互不干扰；
/// · **失败只报错并返回 false**，不抛异常：渲染层据此退化为"这部分不画"，游戏继续跑；
/// · 同一路径的缺失只记一条日志（多次重建不刷屏）。
/// </summary>
internal static class ShaderMaterialFactory
{
	static readonly HashSet<string> ReportedFailures = new();

	/// <summary>
	/// 创建一份新材质。返回 false = shader 不可用（已记录错误），<paramref name="material"/> 为 null。
	/// </summary>
	/// <param name="shaderPath">资源路径（<c>res://...</c>）。</param>
	/// <param name="logTag">日志前缀，如 <c>[FLOW]</c>。</param>
	/// <param name="material">成功时的材质实例（未设任何 uniform）。</param>
	public static bool TryCreate(string shaderPath, string logTag, out ShaderMaterial material)
	{
		material = null;
		if (string.IsNullOrEmpty(shaderPath))
		{
			ReportFailure("-", logTag, "shader 路径为空");
			return false;
		}
		if (ReportedFailures.Contains(shaderPath)) return false;

		Shader shader;
		try
		{
			shader = GD.Load<Shader>(shaderPath);
		}
		catch (Exception e)
		{
			ReportFailure(shaderPath, logTag, $"shader 加载异常：{shaderPath}（{e.Message}）");
			return false;
		}
		if (shader == null)
		{
			ReportFailure(shaderPath, logTag, $"shader 缺失：{shaderPath}");
			return false;
		}

		material = new ShaderMaterial { Shader = shader };
		return true;
	}

	static void ReportFailure(string key, string logTag, string message)
	{
		if (ReportedFailures.Add(key)) GD.PushError($"{logTag} {message}");
	}
}
