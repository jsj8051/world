namespace World.CivSim;

/// <summary>
/// 文明演化模型统一抽象基类（唯一基类；注册表见 CivModelRegistry）。
/// v4 纯实体模型：每个机制 = 一个模型，按 Order 每 tick 执行（docs/石器时代设计.md §二）。
/// ⚠️ 2026-08-23 概念 = 机制组合（Phase 1）：模板方法骨架——机制执行三段式
///   CanApply（机制级适用守卫：频率/存在性）→ Apply（主体行为：内部循环 + 策略调度）→ Post（写后处理）。
///   行为不变：CanApply 默认 true、Post 默认空 ≡ 旧裸 Execute。band 级条件仍在 Apply 内循环判断。
/// </summary>
public abstract class CivModelBase
{
    public abstract string Name { get; }
    public abstract int Order { get; }

    /// <summary>模板方法：机制执行三段式（CanApply → Apply → Post）；子类重写段，不重写本方法。</summary>
    public void Execute(CivSimContext ctx)
    {
        if (!CanApply(ctx)) return;
        Apply(ctx);
        Post(ctx);
    }

    /// <summary>① 机制级适用守卫（频率守卫/前置存在性；默认总是适用）。band 级条件请在 Apply 内判断。</summary>
    protected virtual bool CanApply(CivSimContext ctx) => true;

    /// <summary>② 主体行为（机制积木的核心规则；策略差异走策略查表多态，不做身份 if-else）。</summary>
    protected abstract void Apply(CivSimContext ctx);

    /// <summary>③ 写后处理（派生字段收尾；默认无）。</summary>
    protected virtual void Post(CivSimContext ctx) { }
}
