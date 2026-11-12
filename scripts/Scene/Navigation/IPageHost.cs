using Godot;

namespace World.Scene;

internal interface IPageHost
{
	Node CurrentScreen { get; }
	void MountPage(Node page, LoadOperation transferredOperation = null);
}
