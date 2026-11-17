using System;
using Godot;

namespace World.Utils;

/// <summary>在帧末替换调用方节点，先退出旧子树再激活新子树，避免重叠输入和退出回调重入。</summary>
internal static class SceneReplacement
{
	const string PendingReplacement = "pending_scene_replacement";

	internal static void Replace(Node current, Node next)
	{
		ArgumentNullException.ThrowIfNull(current);
		ArgumentNullException.ThrowIfNull(next);
		if (next.GetParent() != null || next.IsInsideTree())
			throw new ArgumentException("替换节点必须尚未挂载。", nameof(next));
		if (!current.IsInsideTree() || current.HasMeta(PendingReplacement))
		{
			next.Free();
			return;
		}
		current.SetMeta(PendingReplacement, true);
		SceneTree tree = current.GetTree();
		Callable.From(() =>
		{
			if (!GodotObject.IsInstanceValid(current) || !current.IsInsideTree())
			{
				next.Free();
				return;
			}
			Node parent = current.GetParent();
			bool replacesCurrentScene = tree.CurrentScene == current;
			parent.RemoveChild(current);
			current.QueueFree();
			parent.AddChild(next);
			if (replacesCurrentScene) tree.CurrentScene = next;
		}).CallDeferred();
	}
}
