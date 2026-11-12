using System;
using System.Threading;
using Godot;
using World.Data.Generation;
using World.Logic;
using World.Params.Generation;
using World.Scene;

namespace World.Tests;

public partial class GenerationConfigCheck : Node
{
	sealed class Store : IGenerationProfileStore
	{
		public int Writes;
		public GenerationProfileCatalog Load() => new();
		public void Save(GenerationProfileCatalog catalog) { catalog.Validate(); Writes++; }
	}
	sealed class Host : IPageNavigator, IMapOpener, IGenerationStarter, INoticeSink
	{
		public int Starts;
		public GenerationConfig Last;
		public void Navigate<TRequest>(TRequest request) where TRequest : PageRequest { }
		public void OpenMap(string path) { }
		public void Notify(string message, NoticeKind kind = NoticeKind.Info) { }
		public void StartGeography(GenerationConfig config) { Starts++; Last = config; }

		public PageContext Context() => new(CancellationToken.None,
			navigation: this, mapOpener: this, generation: this, notice: this);
	}

	public override async void _Ready()
	{
		try
		{
			string[] args = OS.GetCmdlineUserArgs();
			if (Array.IndexOf(args, "--persistence-write") >= 0 || Array.IndexOf(args, "--persistence-read") >= 0)
			{
				string path = ProjectSettings.GlobalizePath("user://worldgen/verification/profiles.json");
				var sessionCheck = new GenerationConfigSession(new FileGenerationProfileStore(path));
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
			NavigationLogicCheck.Run();
			var app = ResourceLoader.Load<PackedScene>("res://scenes/app/AppRoot.tscn").Instantiate<AppRoot>();
			AddChild(app);
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
				app.Navigation.Navigate(new MapLibraryRequest());
				for (int i = 0; i < 5; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
				GetViewport().GetTexture().GetImage().SavePng("res://tests/map-library-check.png");
				GD.Print("[CONFIG-CHECK] PASS map library capture");
				GetTree().Quit();
				return;
			}
			app.Navigation.Navigate(new GenerationConfigRequest());
			Require(app.CurrentScreen is MapGenerationPage, "Geography did not open config page.");
			Require(app.CurrentScreen.FindChild("WorldManager", true, false) == null, "Config page instantiated world.");
			if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--generate") >= 0)
			{
				var configPage = (MapGenerationPage)app.CurrentScreen;
				configPage.GetNode<World.UI.GenerationConfigEditor>("%Editor").GetNode<SpinBox>("%Seed").Value = 777;
				configPage.GetNode<Button>("%Apply").EmitSignal(Button.SignalName.Pressed);
				configPage.GetNode<Button>("%Generate").EmitSignal(Button.SignalName.Pressed);
				// 生成跑在后台 Job 上；只等固定帧数会在任务结束前就断言。
				ulong started = Time.GetTicksMsec();
				while (Time.GetTicksMsec() - started < 120_000
					&& !(app.CurrentScreen is WorldRoot ready && ready.World?.IsInitialized == true))
					await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
				Require(app.CurrentScreen is WorldRoot root && root.World.IsInitialized && root.Params.Active.Seed == 777,
					"Applied configuration did not initialize real world.");
				GD.Print("[CONFIG-CHECK] PASS real world generation seed=777");
				GetTree().Quit();
				return;
			}
			RemoveChild(app); app.QueueFree();
			var store = new Store();
			var session = new GenerationConfigSession(store);
			var host = new Host();
			var page = ResourceLoader.Load<PackedScene>("res://scenes/app/MapGenerationPage.tscn").Instantiate<MapGenerationPage>();
			page.SetSession(session);
			((IAppPage)page).Configure(host.Context());
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
			page.GetNode<LineEdit>("%ProfileName").Text = "UI profile";
			page.GetNode<ConfirmationDialog>("%NameDialog").EmitSignal(ConfirmationDialog.SignalName.Confirmed);
			page.GetNode<ConfirmationDialog>("%NameDialog").Hide();
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
			var defaultSession = new GenerationConfigSession(new Store());
			var defaultHost = new Host();
			var defaultPage = ResourceLoader.Load<PackedScene>("res://scenes/app/MapGenerationPage.tscn")
				.Instantiate<MapGenerationPage>();
			defaultPage.SetSession(defaultSession);
			((IAppPage)defaultPage).Configure(defaultHost.Context());
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
			"%Workspace", "%Profiles", "%Editor", "%ConfigState", "%Apply", "%Generate", "%Status"
		})
		{
			var control = page.GetNodeOrNull<Control>(path);
			if (control == null) { GD.Print($"[LAYOUT] {path} missing"); continue; }
			Report(shot, path, control);
		}
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
		Check(theme, "TabContainer", "font_selected_color", "TabContainer", "tab_selected", "选中页签");
		Check(theme, "TabContainer", "font_unselected_color", "TabContainer", "tab_unselected", "未选页签");
		Check(theme, "LineEdit", "font_color", "LineEdit", "normal", "文本输入框");
		Check(theme, "Label", "font_color", "AcceptDialog", "panel", "命名对话框");
		Check(theme, "Label", "font_color", "FileDialog", "panel", "文件对话框");
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
