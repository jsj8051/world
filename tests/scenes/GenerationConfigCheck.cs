using System;
using System.IO;
using System.Linq;
using System.Threading;
using Godot;
using World.Application.Generation;
using World.Application.Pages;
using World.Application.Storage;
using World.Data.Generation;
using World.Infrastructure;
using World.Infrastructure.Generation;
using World.Logic;
using World.Infrastructure.Maps;
using World.UI.Pages;

namespace World.Tests;

public partial class GenerationConfigCheck : Node
{
	sealed class Store : IFileStorage<GenerationProfileCatalog>
	{
		public int Writes;
		public GenerationProfileCatalog Import(string path, IFileImportObserver observer = null, CancellationToken cancellation = default) => new();
		public void Export(string path, GenerationProfileCatalog catalog) { catalog.Validate(); Writes++; }
	}
	sealed class Host : IGenerationConfigPageActions
	{
		public int Starts;
		public GenerationConfig Last;
		public void BackToMainMenu() { }
		public void StartGeneration(GenerationConfig config) { Starts++; Last = config; }
	}

	public override async void _Ready()
	{
		try
		{
			string[] args = OS.GetCmdlineUserArgs();
			if (Array.IndexOf(args, "--persistence-write") >= 0 || Array.IndexOf(args, "--persistence-read") >= 0)
			{
				string path = Path.Combine(GamePaths.ConfigDirectory, "verification", "profiles.json");
				var sessionCheck = new GenerationConfigSession(new FileGenerationProfileStore(), path);
				Require(string.IsNullOrEmpty(sessionCheck.LoadProblem), sessionCheck.LoadProblem);
				if (Array.IndexOf(args, "--persistence-write") >= 0)
				{
					foreach (var saved in sessionCheck.Profiles)
						if (saved.Name == "Verification Islands") sessionCheck.Select(saved.Id);
					sessionCheck.Edit(new GenerationConfig { Seed = 20261110, Layout = World.Data.ContinentLayout.Archipelago,
						PlateCount = 12, Rainfall = RainfallPreset.Wet });
					sessionCheck.Save("Verification Islands");
					GD.Print("[CONFIG-CHECK] PASS persistence write: ", path);
				}
				else
				{
					bool found = false;
					foreach (var saved in sessionCheck.Profiles)
						if (saved.Name == "Verification Islands")
						{
							sessionCheck.Select(saved.Id);
							found = true;
						}
					Require(found && sessionCheck.Draft.Seed == 20261110 && sessionCheck.Draft.PlateCount == 12
						&& sessionCheck.Draft.Rainfall == RainfallPreset.Wet, "Saved configuration did not survive process restart.");
					Require(sessionCheck.Applied.Seed == 42 && sessionCheck.HasUnappliedChanges,
						"Process restart restored a persisted Apply state.");
					GD.Print("[CONFIG-CHECK] PASS persistence read in new process; loaded profile is not applied");
				}
				GetTree().Quit();
				return;
			}
			PageShellCheck.Run();
			GD.Print($"[CONFIG-CONTROLLER] PASS {GenerationConfigControllerCheck.Run()} assertions");
			GD.Print($"[GEOGRAPHY-CONTROLLER] PASS {GeographyEvolutionControllerCheck.Run()} assertions");
			GD.Print($"[MAP-CATALOG-CONTROLLER] PASS {MapCatalogControllerCheck.Run()} assertions");
			GD.Print($"[MAP-BROWSER-CONTROLLER] PASS {MapBrowserControllerCheck.Run()} assertions");
			var initialMenu = MainMenuView.Create();
			AddChild(initialMenu);
			Require(GetTree().CurrentScene == this, "Direct page navigation changed the test runner current scene.");
			if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--menu") >= 0)
			{
				for (int i = 0; i < 5; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
				GetViewport().GetTexture().GetImage().SavePng("res://tests/main-menu-check.png");
				GD.Print("[CONFIG-CHECK] PASS main menu capture");
				GetTree().Quit();
				return;
			}
			if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--library") >= 0)
			{
				initialMenu.GetNode<Button>("Center/Panel/Box/LibraryButton").EmitSignal(Button.SignalName.Pressed);
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				var library = FindPage<MapLibraryView>();
				Require(library != null && !GodotObject.IsInstanceValid(initialMenu), "Library action did not replace the menu page.");
				for (int i = 0; i < 4; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
				GetViewport().GetTexture().GetImage().SavePng("res://tests/map-library-check.png");
				GD.Print("[CONFIG-CHECK] PASS map library capture");
				GetTree().Quit();
				return;
			}
			initialMenu.GetNode<Button>("Center/Panel/Box/GeographyButton").EmitSignal(Button.SignalName.Pressed);
			Require(initialMenu.IsInsideTree() && FindPage<GenerationConfigView>() == null,
				"Opening config must defer replacement until frame end.");
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			var navigationConfig = FindPage<GenerationConfigView>();
			Require(navigationConfig != null && !GodotObject.IsInstanceValid(initialMenu), "Geography did not replace the menu with config.");
			Require(navigationConfig.FindChild("WorldManager", true, false) == null, "Config page instantiated world.");
			Require(GetTree().CurrentScene == this, "Opening config replaced the test runner current scene.");
			navigationConfig.GetNode<World.UI.Components.GenerationConfigEditor>("%Editor")
				.GetNode<SpinBox>("%Seed").Value = 987654;
			navigationConfig.GetNode<Button>("%Back").EmitSignal(Button.SignalName.Pressed);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			var returnedMenu = FindPage<MainMenuView>();
			Require(returnedMenu != null && !GodotObject.IsInstanceValid(navigationConfig), "Config back action did not replace the page with a fresh menu.");
			returnedMenu.GetNode<Button>("Center/Panel/Box/GeographyButton").EmitSignal(Button.SignalName.Pressed);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			navigationConfig = FindPage<GenerationConfigView>();
			Require(navigationConfig != null && GetTree().CurrentScene == this,
				"Returning to config did not preserve the test runner scene.");
			Require(navigationConfig.GetNode<World.UI.Components.GenerationConfigEditor>("%Editor")
				.GetNode<SpinBox>("%Seed").Value == new GenerationConfig().Seed,
				"A new configuration page must not inherit the previous page's unsaved draft.");
			if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--generate") >= 0)
			{
				var configPage = FindPage<GenerationConfigView>();
				configPage.GetNode<World.UI.Components.GenerationConfigEditor>("%Editor").GetNode<SpinBox>("%Seed").Value = 777;
				configPage.GetNode<Button>("%Apply").EmitSignal(Button.SignalName.Pressed);
				configPage.GetNode<Button>("%Generate").EmitSignal(Button.SignalName.Pressed);
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				// 生成期间配置页仍在槽位；世界页在生成完成后才挂载。
				Require(GodotObject.IsInstanceValid(configPage) && configPage.IsInsideTree()
					&& FindPage<Node>() is GenerationConfigView,
					"Generation must keep the config page mounted until the world is ready.");
				// 生成跑在后台 Job 上；等世界页挂载。
				ulong started = Time.GetTicksMsec();
				while (Time.GetTicksMsec() - started < 120_000
					&& FindPage<Node>() is not GeographyEvolutionView)
					await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				var worldView = FindPage<GeographyEvolutionView>();
				Require(worldView != null, "Generation did not mount the world page.");
				Require(GetChildCount() == 1 && GetChild(0) == worldView,
					"The world page must be the only page in the slot.");
				while (Time.GetTicksMsec() - started < 120_000
					&& (worldView.World == null || !worldView.World.IsInitialized))
					await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				Require(worldView.World != null && worldView.World.IsInitialized && worldView.Params.Active.Seed == 777,
					"Applied configuration did not initialize real world.");
				Require(FindPage<Node>() == worldView && GetChildCount() == 1,
					"The world page must stay mounted after assembly.");
				Require(worldView.GetNode<CanvasLayer>("AppMenu").Visible,
					"The world chrome must stay visible after generation.");
				if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--roundtrip") >= 0)
				{
					Require(GetChildCount() == 1 && GetChild(0) == worldView,
						"Only the world page may stay mounted after generation.");
					var menu = worldView.GetNode<World.UI.Components.AppMenu>("AppMenu");
					menu.GetNode<Button>("MenuButton").EmitSignal(Button.SignalName.Pressed);
					Button backButton = null;
					foreach (Node item in menu.GetNode("Panel/Box/Items").GetChildren())
						if (item is Button { Text: "返回主界面" } button) backButton = button;
					Require(backButton != null, "The world menu has no back item.");
					backButton.EmitSignal(Button.SignalName.Pressed);
					Require(GodotObject.IsInstanceValid(worldView) && worldView.IsInsideTree(),
						"Navigation must defer replacing the world page until frame end.");
					for (int i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
					Require(FindPage<Node>() is MainMenuView, "Roundtrip did not return to the main menu.");
					Require(!GodotObject.IsInstanceValid(worldView) || !worldView.IsInsideTree(),
						"The world page stayed inside the tree after navigating away.");
					Require(GetChildCount() == 1, "The page host kept more than one page.");
					FindPage<Node>().GetNode<Button>("Center/Panel/Box/GeographyButton")
						.EmitSignal(Button.SignalName.Pressed);
					for (int i = 0; i < 5; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
					Require(FindPage<Node>() is GenerationConfigView, "Roundtrip did not reopen the config page.");
					Require(GetChildCount() == 1, "The page host kept more than one page after opening config.");
					if (DisplayServer.GetName() != "headless")
					{
						await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
						GetViewport().GetTexture().GetImage().SavePng("res://tests/roundtrip-config-check.png");
					}
					GD.Print("[CONFIG-CHECK] PASS loading overlay→world→menu→config roundtrip");
					GetTree().Quit();
					return;
				}
				GD.Print("[CONFIG-CHECK] PASS real world generation seed=777");
				GetTree().Quit();
				return;
			}
			RemoveChild(navigationConfig);
			navigationConfig.QueueFree();
			var store = new Store();
			var session = new GenerationConfigSession(store, "profiles.json");
			var host = new Host();
			var page = ResourceLoader.Load<PackedScene>("res://scenes/pages/GenerationConfig/GenerationConfigPage.tscn").Instantiate<GenerationConfigView>();
			page.SetController(new GenerationConfigController(session, host));
			AddChild(page);
			var editor = page.GetNode<TabContainer>("%Editor");
			Require(editor.GetTabCount() == 4 && editor.GetTabTitle(3) == "气候", "Tabs missing.");
			editor.GetNode<SpinBox>("%Seed").Value = 12345;
			page.GetNode<Button>("%Apply").EmitSignal(Button.SignalName.Pressed);
			Require(session.Applied.Seed == 12345 && store.Writes == 0, "Apply persisted or lost seed.");
			editor.GetNode<SpinBox>("%Seed").Value = 12346;
			page.GetNode<Button>("%Generate").EmitSignal(Button.SignalName.Pressed);
			Require(host.Starts == 0, "Unapplied edits generated.");
			page.GetNode<Button>("%Save").EmitSignal(Button.SignalName.Pressed);
			var profilePanel = page.GetNode<World.UI.Components.ProfileLibraryPanel>("%ProfileLibrary");
			profilePanel.GetNode<LineEdit>("%ProfileName").Text = "UI profile";
			profilePanel.GetNode<ConfirmationDialog>("%NameDialog").EmitSignal(ConfirmationDialog.SignalName.Confirmed);
			profilePanel.GetNode<ConfirmationDialog>("%NameDialog").Hide();
			Require(store.Writes == 1 && session.Profiles.Count == 1 && session.Applied.Seed == 12345, "Save applied or failed.");
			page.GetNode<Button>("%Apply").EmitSignal(Button.SignalName.Pressed);
			page.GetNode<Button>("%Generate").EmitSignal(Button.SignalName.Pressed);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			Require(host.Starts == 1 && host.Last.Seed == 12346, "Generation config or duplicate gate failed.");
			Require(editor.Size.X >= 720 && editor.Size.Y >= 240, "Editor was collapsed.");
			for (int i = 0; i < 4; i++)
			{
				editor.CurrentTab = i;
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				var tab = editor.GetTabControl(i);
				Require(tab.Size.X > 0 && tab.Size.Y > 0, "A configuration tab has no usable area.");
			}
			editor.CurrentTab = 0;
			RequireReadableContrast(page);
			string[] argv = OS.GetCmdlineUserArgs();
			int tabIndex = Array.IndexOf(argv, "--tab");
			if (tabIndex >= 0 && tabIndex + 1 < argv.Length && int.TryParse(argv[tabIndex + 1], out int wanted))
			{
				editor.CurrentTab = Math.Clamp(wanted, 0, 3);
				for (int i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			}
			if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--layout") >= 0)
			{
				await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
				DumpLayout(page, editor);
			}
			if (DisplayServer.GetName() != "headless")
			{
				await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
				GetViewport().GetTexture().GetImage().SavePng("res://tests/generation-config-check.png");
			}
			// 默认配置已应用：全新会话进入页面即显示“已应用”，无需点应用就能开始生成。
			page.QueueFree();
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			var defaultSession = new GenerationConfigSession(new Store(), "profiles.json");
			var defaultHost = new Host();
			var defaultPage = ResourceLoader.Load<PackedScene>("res://scenes/pages/GenerationConfig/GenerationConfigPage.tscn")
				.Instantiate<GenerationConfigView>();
			defaultPage.SetController(new GenerationConfigController(defaultSession, defaultHost));
			AddChild(defaultPage);
			Require(!defaultSession.HasUnappliedChanges, "A fresh session must start applied.");
			Require(defaultPage.GetNode<Label>("%ConfigState").Text == "已应用", "Config state did not report applied defaults.");
			Require(!defaultPage.GetNode<Button>("%Generate").Disabled, "Generate was disabled before any edit.");
			defaultPage.GetNode<Button>("%Generate").EmitSignal(Button.SignalName.Pressed);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			Require(defaultHost.Starts == 1 && defaultHost.Last.Seed == 42,
				"Applied defaults did not start generation directly.");
			GD.Print("[CONFIG-CHECK] PASS navigation, tabs, apply, named save, dirty gate, default-applied generation");
			GetTree().Quit();
		}
		catch (Exception error) { GD.PushError($"[CONFIG-CHECK] {error}"); GetTree().Quit(1); }
	}
	T FindPage<T>() where T : Node => GetChildren().OfType<T>().SingleOrDefault();

	static void Require(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}

	/// <summary>校验文字与底色的实际主题对比度（WCAG 正文门槛 4.5:1）。</summary>
	static void DumpLayout(Control page, TabContainer editor)
	{
		Image shot = page.GetViewport().GetTexture().GetImage();
		GD.Print($"[LAYOUT] viewport={shot.GetWidth()}x{shot.GetHeight()} page={page.Size} editor={editor.Size}");
		foreach (string path in new[]
		{
			"%Workspace", "%Editor", "%ConfigState", "%Apply", "%Generate", "%Status"
		})
		{
			var control = page.GetNodeOrNull<Control>(path);
			if (control == null) { GD.Print($"[LAYOUT] {path} missing"); continue; }
			Report(shot, path, control);
		}
		var profiles = page.GetNodeOrNull<Control>("%ProfileLibrary")?.GetNodeOrNull<Control>("%Profiles");
		if (profiles != null) Report(shot, "%Profiles", profiles);
		else GD.Print("[LAYOUT] %Profiles missing");
		var basic = editor.GetTabControl(0);
		Report(shot, "Basic/Title", basic.GetNode<Label>("Title"));
		Report(shot, "Basic/Seed", editor.GetNode<SpinBox>("%Seed"));
	}

	static void Report(Image shot, string label, Control control)
	{
		Color font = control.GetThemeColor("font_color", control.GetClass());
		Rect2 rect = control.GetGlobalRect();
		float scale = shot.GetWidth() / Math.Max(1f, control.GetViewportRect().Size.X);
		Rect2I area = new((Vector2I)(rect.Position * scale), (Vector2I)(rect.Size * scale));
		area = area.Intersection(new Rect2I(Vector2I.Zero, shot.GetSize()));
		float darkest = 1f;
		Vector2I at = Vector2I.Zero;
		if (area.Size.X > 0 && area.Size.Y > 0)
		{
			for (int y = area.Position.Y; y < area.End.Y; y++)
				for (int x = area.Position.X; x < area.End.X; x++)
				{
					Color c = shot.GetPixel(x, y);
					float l = c.R * 0.299f + c.G * 0.587f + c.B * 0.114f;
					if (l < darkest) { darkest = l; at = new Vector2I(x, y); }
				}
		}
		GD.Print($"[LAYOUT] {label} scale={scale:F2} rect={area} font={font.ToHtml()} " +
			$"darkest={darkest:F2}@{at}");
	}

	static void RequireReadableContrast(Control page)
	{
		var theme = page.Theme ?? throw new InvalidOperationException("Config page has no theme.");
		Check(theme, "Label", "font_color", "PanelContainer", "panel", "页面正文");
		Check(theme, "ItemList", "font_color", "ItemList", "panel", "配置列表");
		Check(theme, "Button", "font_color", "Button", "normal", "按钮");
		Check(theme, "Button", "font_disabled_color", "Button", "disabled", "禁用按钮");
		Check(theme, "TabContainer", "font_selected_color", "TabContainer", "tab_selected", "选中页签");
		Check(theme, "TabContainer", "font_unselected_color", "TabContainer", "tab_unselected", "未选页签");
		Check(theme, "LineEdit", "font_color", "LineEdit", "normal", "文本输入框");
		Check(theme, "Label", "font_color", "AcceptDialog", "panel", "命名对话框");
		Require(theme.HasStylebox("tab_selected", "TabContainer") && theme.HasStylebox("tab_unselected", "TabContainer"),
			"Tab styles are missing from the app theme.");
	}

	static void Check(Theme theme, string fontType, string fontName, string styleType, string styleName, string what)
	{
		if (!theme.HasColor(fontName, fontType) || !theme.HasStylebox(styleName, styleType))
			throw new InvalidOperationException($"{what} 缺少主题配色。");
		if (theme.GetStylebox(styleName, styleType) is not StyleBoxFlat box)
			throw new InvalidOperationException($"{what} 的底色不是纯色，无法校验对比度。");
		double ratio = Contrast(theme.GetColor(fontName, fontType), box.BgColor);
		GD.Print($"[CONFIG-CHECK] contrast {what} = {ratio:F2}:1");
		Require(ratio >= 4.5, $"{what} 对比度过低（{ratio:F2}:1），会看不清。");
	}

	static double Contrast(Color a, Color b)
	{
		double la = Luminance(a), lb = Luminance(b);
		if (la < lb) (la, lb) = (lb, la);
		return (la + 0.05) / (lb + 0.05);
	}

	static double Luminance(Color c) =>
		0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

	static double Linear(float channel) =>
		channel <= 0.03928f ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
}
