using Godot;

namespace World.Services;

/// <summary>
/// 日志收编（L2 服务层，ADR-0002；全量迁移范围见 ADR-0004）。
/// 薄封装 GD.Print/GD.PrintErr，统一 "[标签] 消息" 前缀格式。
/// ⚠️ 纪律：后台线程禁止调用（Godot 输出线程不安全——沿用项目既有 log:false 参数模式；
/// 后台线程的低频错误打印保持 GD.Print 直调 + 注释，见 ADR-0004 §决策4）。
/// </summary>
public static class LogService
{
    public static void Log(string tag, string message) => GD.Print($"[{tag}] {message}");

    public static void LogErr(string tag, string message) => GD.PrintErr($"[{tag}] {message}");
}
