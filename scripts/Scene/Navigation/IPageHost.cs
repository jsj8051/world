using System;
using Godot;
using World.Application.Loading;

namespace World.Scene;

/// <summary>页面宿主契约：导航只依赖挂载、当前页与延迟回调三项能力。</summary>
internal interface IPageHost
{
	Node CurrentScreen { get; }
	void MountPage(Node page, LoadOperation transferredOperation = null);
	void Defer(Action action);
}
