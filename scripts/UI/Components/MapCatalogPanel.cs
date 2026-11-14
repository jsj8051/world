using System;
using Godot;
using World.Application.Components;

namespace World.UI.Components;

/// <summary>
/// 地图目录组件：存档列表、选中详情、加载按钮与状态行。
/// 目录探测、选择与加载意图归 <see cref="MapCatalogController"/>；本类只重绘控件。
/// </summary>
public partial class MapCatalogPanel : VBoxContainer
{
	static readonly Color ErrorInk = new("#FFB4A8");

	MapCatalogController _controller;
	ItemList _list;
	Label _detail;
	Button _open;
	Label _status;

	/// <summary>挂接组件控制器；页面在创建控制器后调用一次。</summary>
	internal void Bind(MapCatalogController controller)
	{
		_controller = controller ?? throw new ArgumentNullException(nameof(controller));
		_controller.CatalogChanged += RefreshCatalog;
		_controller.StateChanged += RefreshState;
		RefreshCatalog();
	}

	public override void _Ready()
	{
		_list = GetNode<ItemList>("%List");
		_detail = GetNode<Label>("%Detail");
		_open = GetNode<Button>("%OpenButton");
		_status = GetNode<Label>("%Status");
		_list.ItemSelected += index => _controller?.Select((int)index);
		_open.Pressed += () => _controller?.LoadSelected();
	}

	public override void _ExitTree()
	{
		if (_controller == null) return;
		_controller.CatalogChanged -= RefreshCatalog;
		_controller.StateChanged -= RefreshState;
		_controller = null;
	}

	void RefreshCatalog()
	{
		_list.Clear();
		foreach (MapCatalogEntry entry in _controller.Entries) _list.AddItem(entry.DisplayName);
		if (_controller.SelectedIndex >= 0) _list.Select(_controller.SelectedIndex);
		RefreshState();
	}

	void RefreshState()
	{
		_open.Disabled = !_controller.CanLoad;
		_detail.Text = _controller.DetailText;
		_status.Text = _controller.StatusText;
		_status.Visible = _controller.StatusVisible;
		_status.Modulate = _controller.StatusIsError ? ErrorInk : Colors.White;
	}
}
