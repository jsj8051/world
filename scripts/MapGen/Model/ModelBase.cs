using System;

namespace World.MapGen.Model;

/// <summary>
/// 模型统一抽象基类（2026-08-16 用户拍板：基类必须统一）。
///
/// 【所有模型都继承这一个基类】——不限于气候：温度场、风场、降水场、洋流、
/// 反馈环、未来的水文/矿藏/土壤系统，只要符合"场→结论→消费"这套逻辑，
/// 都继承 ModelBase。角色能力（场=计算产出、环=反馈修正）通过接口附加
/// （IFieldRole/ILoopRole），不产生第二基类。
///
/// 共性（所有模型必有）：
///   名称 Name、量级 Magnitude（精度截断判据）、验证 Verify、依赖 DependsOn
///
/// 不变量：
///   1. 流水线单向：每场只算一次（闭环修正都在源头场入口，下游结构不变）
///   2. 复杂度 O(环数)：环与环不嵌套，每环独立决策、独立验证
///   3. 封版判据：环清单完备 + 每环有决策 → 复杂度冻结
/// </summary>
public abstract class ModelBase
{
    /// <summary>模型名称（与 docs/ 文档对照）。</summary>
    public abstract string Name { get; }

    /// <summary>量级（精度截断判据：修正 &lt; 量级×阈值 → 截断）。所有模型共用。</summary>
    public abstract float Magnitude { get; }

    /// <summary>验证：产出状态/决策完整性。注册表多态遍历调用。</summary>
    public virtual bool Verify() => true;

    /// <summary>依赖声明（流水线拓扑/一致性检查）。默认无。</summary>
    public virtual string[] DependsOn() => Array.Empty<string>();

    /// <summary>人类可读摘要。</summary>
    public override string ToString() => $"{Name} 量级{Magnitude}";
}

/// <summary>
/// 场角色能力（接口，非基类）：网格连续量，计算产出。
/// 实现者：TemperatureField / UnifiedWindField / ...（继承 ModelBase + 本接口）
/// </summary>
public interface IFieldRole
{
    /// <summary>定义域：全球 / 海洋 / 陆地。</summary>
    string Domain { get; }

    /// <summary>产生阶段（Stage1/Stage2/... 或 板块）。</summary>
    string Stage { get; }

    /// <summary>计算该场。</summary>
    void Compute();
}

/// <summary>
/// 环角色能力（接口，非基类）：反馈回路（A→B→A），三态决策 + 修正行为。
///   Closed  已闭环：一步解析收敛（负反馈 1/(1+g)），修正加在源头场
///   Cut     单向截断：影响 &lt; 精度阈值，保持单向
///   Ignored 忽略：低于下一阶，记录理由
/// 实现者：WetCoolingLoop / ...（继承 ModelBase + 本接口）
/// </summary>
public interface ILoopRole
{
    /// <summary>三态：Closed / Cut / Ignored。</summary>
    string Status { get; }

    /// <summary>决策记录（为什么闭/截断/忽略）。</summary>
    string Decision { get; }

    /// <summary>闭环行为（Closed 环实现；Cut/Ignored 默认无操作）。</summary>
    void Apply();
}
