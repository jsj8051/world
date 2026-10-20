using System;
using System.Reflection;

namespace World.Data;

/// <summary>
/// 参数实例的逐属性复制：把源实例的全部公开、可读写实例属性写进目标实例，不动目标实例身份。
/// 段对象（<see cref="WorldSpec.Plate"/> / <see cref="WorldSpec.Terrain"/>）按段处理，不在这里整体替换。
/// </summary>
static class SpecCopy
{
	/// <summary>复制 <typeparamref name="T"/> 的全部公开可写属性。</summary>
	internal static void CopyProperties<T>(T source, T target) where T : class
	{
		if (source == null) throw new ArgumentNullException(nameof(source));
		if (target == null) throw new ArgumentNullException(nameof(target));

		foreach (var p in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length != 0) continue;
			p.SetValue(target, p.GetValue(source));
		}
	}
}
