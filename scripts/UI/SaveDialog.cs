using Godot;
using System;
using System.IO;

using World.CivSim;
using World.Gameplay;
using World.LogicGrid;
using World.Services;

namespace World.UI;

/// <summary>
/// 游玩保存链组件（2026-08-31 拆分：原 MapViewer.Ui.cs 的保存按钮/槽名模态框/Toast 独立成组件）。
/// 四层模型：表现+容器——保存按钮场景预置（scenes/ui/EpochPanel.tscn EpochRow/SaveBtn，样式 sb_hud_btn 单一来源=场景）；
/// 模态框/Toast 懒建挂 UiLayer（数据驱动/懒加载，ui-architecture 合理偏离 #3 可接受）。
/// 逻辑层：本组件——槽名默认值（读 EpochBar 标签文本只读派生）/输入校验/保存执行/Toast。
/// 数据：游玩形态与保存原件（_gameGrid/_civResult/_mapRefPath）经 SetSaveState 下行注入——
/// 组件不反向持有 MapViewer 状态，保存动作只调 SaveArchive 服务（数据层）。
/// </summary>
public partial class SaveDialog : Button
{
    // ── 模态框节点（懒建一次，挂 UiLayer CanvasLayer）──
    private ColorRect _saveDim;       // 槽名输入模态框遮罩（全屏拦截点击）
    private PanelContainer _saveBox;  // 槽名输入面板（遮罩内居中）
    private LineEdit _saveInput;      // 槽名输入框（Enter 即保存）
    private PanelContainer _saveToast; // 保存结果 Toast（底部中央，自动淡出）
    private CanvasLayer _ui;          // 挂载点（祖先 UiLayer；懒建模态框/Toast 挂这里）

    // ── 保存数据（读档完成时下行注入；DoSave 唯一数据源）──
    private bool _playMode;           // 游玩形态（浏览形态不显示保存按钮）
    private bool _canSave;            // 数据就绪（读档完成 + 网格/结果非空）
    private GameGrid _gameGrid;       // 完整逻辑网格（ToMapData 不复制 Psi 且丢 grid——保存必须持原件）
    private CivSimResult _civResult;  // 演化结果快照（含 FinalTick/Player——保存写回的唯一来源）
    private string _mapRefPath;       // 本局来源地图（.sav 的 REFS 段；.cmp 无 REFS → null = 直开档）

    public override void _Ready()
    {
        _ui = GetNodeOrNull<CanvasLayer>("../../..");   // SaveBtn → EpochRow → EpochPanel → UiLayer
        Pressed += ShowSaveDialog;
        RefreshSaveButton();
    }

    /// <summary>下行：注入保存状态（MapViewer 读档完成/演化完成路径调用）。
    /// playMode=游玩形态（按钮可见）；canSave=数据就绪（可点）；原件供 DoSave 使用。</summary>
    public void SetSaveState(bool playMode, bool canSave, GameGrid grid, CivSimResult result, string mapRefPath)
    {
        _playMode = playMode;
        _canSave = canSave;
        _gameGrid = grid;
        _civResult = result;
        _mapRefPath = mapRefPath;
        RefreshSaveButton();
    }

    /// <summary>刷新保存按钮可用态（游玩形态 + 文明档已载入才可保存；读档/生成完成路径调用）。</summary>
    private void RefreshSaveButton()
    {
        Visible = _playMode;   // 浏览形态不显示（只有游玩中才产生存档）
        Disabled = !_canSave;
    }

    /// <summary>默认槽名：纪元 + 演化年（如「旧石器 演化 1,200 年」）；无文明 → 时间戳。
    /// 纪元文本读 EpochBar 的标签（同场景内，只读派生）。</summary>
    private string DefaultSlotName()
    {
        var epoch = GetNodeOrNull<Label>("../EpochLabel");
        var year = GetNodeOrNull<Label>("../YearLabel");
        string ep = epoch?.Text?.TrimStart('◆', ' ') ?? "世界";
        string yr = year?.Text?.Trim() ?? "";
        return yr.Length == 0 ? $"{ep} 存档" : $"{ep} {yr}";
    }

    private void ShowSaveDialog()
    {
        EnsureSaveDialog();
        _saveInput.Text = DefaultSlotName();
        _saveDim.Visible = true;
        _saveBox.Visible = true;
        _saveInput.GrabFocus();
        _saveInput.SelectAll();
    }

    private void HideSaveDialog()
    {
        if (_saveDim != null) _saveDim.Visible = false;
        if (_saveBox != null) _saveBox.Visible = false;
    }

    /// <summary>懒建槽名输入模态框（遮罩 + 居中面板 + 输入框 + 取消/保存）。样式复用 SaveRowStyle 羊皮纸色板。</summary>
    private void EnsureSaveDialog()
    {
        if (_saveBox != null) return;
        if (_ui == null) return;   // 场景缺 UiLayer → 静默（同 CukHud 韧性约定）

        _saveDim = new ColorRect
        {
            Name = "SaveDim",
            Color = new Color(0f, 0f, 0f, 0.45f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        _saveDim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _ui.AddChild(_saveDim);

        var center = new CenterContainer { Name = "SaveCenter", MouseFilter = Control.MouseFilterEnum.Ignore };
        center.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _saveDim.AddChild(center);

        _saveBox = new PanelContainer { Name = "SaveBox", CustomMinimumSize = new Vector2(430, 0) };
        _saveBox.AddThemeStyleboxOverride("panel", SaveRowStyle.CardStyle());
        center.AddChild(_saveBox);

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 12);
        _saveBox.AddChild(v);

        var title = new Label { Text = "💾 保存游戏" };
        title.AddThemeFontSizeOverride("font_size", 19);
        title.AddThemeColorOverride("font_color", SaveRowStyle.Fg);
        v.AddChild(title);

        var hint = new Label
        {
            Text = "存入「加载存档」列表（.sav——世界快照 + 玩家状态 + 来源地图一并保存）",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.AddThemeColorOverride("font_color", SaveRowStyle.Muted);
        v.AddChild(hint);

        _saveInput = new LineEdit
        {
            Name = "SaveInput",
            PlaceholderText = "存档名称（同名将覆盖旧存档）",
            CustomMinimumSize = new Vector2(0, 42),
        };
        _saveInput.TextSubmitted += _ => DoSave();   // Enter 即保存
        v.AddChild(_saveInput);

        var btns = new HBoxContainer();
        btns.AddThemeConstantOverride("separation", 10);
        btns.Alignment = BoxContainer.AlignmentMode.End;
        v.AddChild(btns);

        var cancel = new Button { Name = "SaveCancel", Text = "取消", CustomMinimumSize = new Vector2(96, 40) };
        cancel.AddThemeFontSizeOverride("font_size", 14);
        cancel.AddThemeStyleboxOverride("normal", SaveRowStyle.GhostNormal());
        cancel.AddThemeStyleboxOverride("hover", SaveRowStyle.GhostHover());
        cancel.Pressed += HideSaveDialog;
        btns.AddChild(cancel);

        var ok = new Button { Name = "SaveOk", Text = "保存", CustomMinimumSize = new Vector2(96, 40) };
        ok.AddThemeFontSizeOverride("font_size", 14);
        ok.AddThemeStyleboxOverride("normal", SaveRowStyle.PrimaryNormal());
        ok.AddThemeStyleboxOverride("hover", SaveRowStyle.PrimaryHover());
        ok.Pressed += DoSave;
        btns.AddChild(ok);

        HideSaveDialog();
    }

    /// <summary>执行保存：槽名清洗（Windows 非法文件名字符 → _）→ SaveArchive.Write → Toast。</summary>
    private void DoSave()
    {
        string name = _saveInput.Text.Trim();
        if (name.Length == 0) name = DefaultSlotName();
        var bad = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name) sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
        name = sb.ToString();

        bool ok = false;
        try
        {
            ok = SaveArchive.Write(name, _gameGrid, _civResult, _mapRefPath);
        }
        catch (Exception ex)
        {
            LogService.LogErr("SaveDialog", $"保存失败 {name}: {ex}");
        }
        HideSaveDialog();
        if (ok) LogService.Log("SaveDialog", $"saved slot={name} ref={_mapRefPath ?? "(无)"}");
        ShowSaveToast(ok ? $"💾 已保存：{name}" : "⚠️ 保存失败（详情见日志）");
    }

    /// <summary>保存结果 Toast（底部中央，淡入 → 停留 → 淡出）。懒建一次复用。</summary>
    private void ShowSaveToast(string text)
    {
        if (_ui == null) return;
        if (_saveToast == null)
        {
            _saveToast = new PanelContainer();
            _saveToast.AddThemeStyleboxOverride("panel", SaveRowStyle.ToastStyle());
            _saveToast.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
            _saveToast.Position = new Vector2(-140, -90);   // 居中：宽约 280 → 左移一半，贴底偏上
            _ui.AddChild(_saveToast);
            var lbl = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
            lbl.AddThemeFontSizeOverride("font_size", 15);
            lbl.AddThemeColorOverride("font_color", SaveRowStyle.Fg);
            lbl.CustomMinimumSize = new Vector2(280, 40);
            _saveToast.AddChild(lbl);
            _saveToast.SetMeta("lbl", lbl);
        }
        var label = _saveToast.GetMeta("lbl").As<Label>();
        label.Text = text;
        _saveToast.Modulate = new Color(1f, 1f, 1f, 0f);
        _saveToast.Visible = true;
        var tw = _saveToast.CreateTween();
        tw.TweenProperty(_saveToast, "modulate:a", 1f, 0.18f);
        tw.TweenInterval(1.6f);
        tw.TweenProperty(_saveToast, "modulate:a", 0f, 0.4f);
        tw.TweenCallback(Callable.From(() => _saveToast.Visible = false));
    }
}