using System;
using Godot;
using World.Application.Components;
using World.Data.Generation;

namespace World.UI.Components;

/// <summary>
/// 已保存配置组件：配置列表、重命名/删除按钮与名称/确认两个对话框。
/// 选择、命名与确认流归 <see cref="ProfileLibraryController"/>；本类只按状态重绘控件。
/// </summary>
public partial class ProfileLibraryPanel : VBoxContainer
{
	ProfileLibraryController _controller;
	ItemList _profiles;
	Button _rename;
	Button _delete;
	ConfirmationDialog _nameDialog;
	ConfirmationDialog _confirmDialog;
	LineEdit _profileName;

	/// <summary>挂接组件控制器；页面在创建控制器后调用一次。</summary>
	internal void Bind(ProfileLibraryController controller)
	{
		_controller = controller ?? throw new ArgumentNullException(nameof(controller));
		_controller.StateChanged += ApplyState;
		ApplyState();
	}

	/// <summary>独立运行或页面禁用时，让控件不可交互。</summary>
	internal void SetInteractive(bool interactive)
	{
		_profiles.MouseFilter = interactive ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
		_rename.Disabled = !interactive;
		_delete.Disabled = !interactive;
	}

	public override void _Ready()
	{
		_profiles = GetNode<ItemList>("%Profiles");
		_rename = GetNode<Button>("%Rename");
		_delete = GetNode<Button>("%Delete");
		_nameDialog = GetNode<ConfirmationDialog>("%NameDialog");
		_confirmDialog = GetNode<ConfirmationDialog>("%ConfirmDialog");
		_profileName = GetNode<LineEdit>("%ProfileName");

		_profiles.ItemSelected += index => _controller?.Select((int)index);
		_rename.Pressed += () => _controller?.RequestRename();
		_delete.Pressed += () => _controller?.RequestDelete();
		_nameDialog.Confirmed += () => _controller?.SubmitName(_profileName.Text);
		_profileName.TextSubmitted += _ =>
		{
			_nameDialog.Hide();
			_controller?.SubmitName(_profileName.Text);
		};
		_nameDialog.Canceled += () => _controller?.CancelName();
		_confirmDialog.Confirmed += () => _controller?.Confirm();
		_confirmDialog.Canceled += () => _controller?.CancelConfirm();
		_nameDialog.GetOkButton().Text = "确认";
	}

	public override void _ExitTree()
	{
		if (_controller != null) _controller.StateChanged -= ApplyState;
		_controller = null;
	}

	void ApplyState()
	{
		if (_controller == null) return;
		GenerationProfile[] profiles = _controller.Profiles;
		int selected = _controller.SelectedIndex;
		_profiles.Clear();
		for (int i = 0; i < profiles.Length; i++)
		{
			_profiles.AddItem(profiles[i].Name);
			if (i == selected) _profiles.Select(i);
		}
		_rename.Disabled = !_controller.HasSelection;
		_delete.Disabled = !_controller.HasSelection;

		if (_controller.NameDialog is { } name)
		{
			if (!_nameDialog.Visible)
			{
				_nameDialog.Title = name.Title;
				_profileName.Text = name.InitialName;
				_nameDialog.PopupCentered(new Vector2I(480, 180));
				_profileName.GrabFocus();
				_profileName.SelectAll();
			}
		}
		else if (_nameDialog.Visible)
		{
			_nameDialog.Hide();
		}

		if (_controller.Confirmation is { } confirmation)
		{
			if (!_confirmDialog.Visible)
			{
				_confirmDialog.GetOkButton().Text = confirmation.OkText;
				_confirmDialog.DialogText = confirmation.Text;
				_confirmDialog.PopupCentered();
			}
		}
		else if (_confirmDialog.Visible)
		{
			_confirmDialog.Hide();
		}
	}
}
