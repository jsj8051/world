namespace World.MapGen.Model;

/// <summary>具体反馈环实现（2026-08-16）：继承统一基类 ModelBase + 环角色接口 ILoopRole，
/// 三态决策记录在案。⚠️ 角色差异走接口，不产生第二基类。</summary>

/// <summary>环1：温度→风→降水→温度（湿润降温，负反馈一步解析收敛）。</summary>
public sealed class WetCoolingLoop : ModelBase, ILoopRole
{
    private readonly PlanetPipeline _pipe;
    public WetCoolingLoop(PlanetPipeline pipe) => _pipe = pipe;
    public override string Name => "温度→风→降水→温度";
    public string Status => "Closed";
    public override float Magnitude => 4f;
    public override string[] DependsOn() => new[] { "年均温", "年降水" };   // 年温年降水后 Apply，月温度场依赖本环
    public string Decision =>
        "湿润降温 T=T₀−k·P/(1+g)，k=0.004°C/mm、g=0.3（负反馈增益）。修正加在源头温度（Stage1 入口，年温年降水之后、MonsoonSystem 之前）";

    /// <summary>
    /// 闭环行为（2026-08-16 实现）：一步解析收敛。
    /// 物理：降水多 = 蒸发强 + 云反照率 → 湿润区凉（亚马逊 vs 撒哈拉同纬度差 2-4°C）。
    /// 负反馈自我抑制：凉 → 海陆温差小 → 风弱 → 雨少 → 凉得少 → 平衡修正 = 初始/(1+g)。
    /// 只降陆地湿润区（海洋格 Precip=0 不变）。
    /// </summary>
    public void Apply()
    {
        if (_pipe.Precip == null || _pipe.Temp == null) return;
        const float k = 0.004f;   // °C/mm：年降水 1000mm → 4°C（湿润降温量级）
        const float g = 0.3f;     // 负反馈增益（自我抑制 30%）
        for (int i = 0; i < _pipe.Temp.Length; i++)
            if (_pipe.Precip[i] > 0f)
                _pipe.Temp[i] -= k * _pipe.Precip[i] / (1f + g);
    }
}

/// <summary>环2：温度→洋流→温度（双向：洋流修正 + 热成风）。</summary>
public sealed class ThermalCurrentLoop : ModelBase, ILoopRole
{
    public override string Name => "温度→洋流→温度";
    public string Status => "Closed";
    public override float Magnitude => 5f;
    public void Apply() { }   // 闭环行为：Closed 环后续实现，Cut/Ignored 无操作
    public string Decision =>
        "双向已内联：洋流修正进 ClimateGenerator 温度公式；温度进 OceanCurrent 热成风。" +
        "温度修正用第一遍 WindField 洋流（生成顺序限制），第二遍统一风场洋流只覆盖存档场——接受。Apply 已实现（OceanCurrent/ClimateGenerator 内联）";
}

/// <summary>环3：温度→冰盖→反照率→温度（截断）。</summary>
public sealed class IceAlbedoLoop : ModelBase, ILoopRole
{
    public override string Name => "温度→冰盖→反照率→温度";
    public string Status => "Cut";
    public override float Magnitude => 2f;
    public void Apply() { }   // 闭环行为：Closed 环后续实现，Cut/Ignored 无操作
    public string Decision => "反照率按纬度+海拔固定阈值（>60° 冰、>0.5 海拔雪），不随温度演化冰盖边界。误差小，截断";
}

/// <summary>环4：植被→降水→植被（截断）。</summary>
public sealed class VegetationLoop : ModelBase, ILoopRole
{
    public override string Name => "植被→降水→植被";
    public string Status => "Cut";
    public override float Magnitude => 0f;
    public void Apply() { }   // 闭环行为：Closed 环后续实现，Cut/Ignored 无操作
    public string Decision => "生物群系是 Stage1 输出，不回流气候。植被蒸散局地 ±10% 忽略";
}

/// <summary>环5：云→辐射→温度（忽略）。</summary>
public sealed class CloudRadiationLoop : ModelBase, ILoopRole
{
    public override string Name => "云→辐射→温度";
    public string Status => "Ignored";
    public override float Magnitude => 2f;
    public void Apply() { }   // 闭环行为：Closed 环后续实现，Cut/Ignored 无操作
    public string Decision => "低于精度阈值（湿润降温已代表主要云效应），标注为下一阶";
}

/// <summary>环6：CO₂/温室气体（忽略）。</summary>
public sealed class GreenhouseLoop : ModelBase, ILoopRole
{
    public override string Name => "CO₂/温室气体";
    public string Status => "Ignored";
    public override float Magnitude => 0f;
    public void Apply() { }   // 闭环行为：Closed 环后续实现，Cut/Ignored 无操作
    public string Decision => "星球大气固定（无大气演化模型），温室隐含在 ClimateGenerator 标定曲线";
}

/// <summary>环7：海拔→温度→侵蚀→海拔（截断，架构单向流）。</summary>
public sealed class ErosionLoop : ModelBase, ILoopRole
{
    public override string Name => "海拔→温度→侵蚀→海拔";
    public string Status => "Cut";
    public override float Magnitude => 0f;
    public void Apply() { }   // 闭环行为：Closed 环后续实现，Cut/Ignored 无操作
    public string Decision => "侵蚀堆积（双介质：水蚀坡面+风蚀搬运沿风场沉积）改海拔（山地夷平/低地堆积/河谷三角洲），不回流 Stage1 气候（两层架构单向流保证）。生成一次，非长期演化（板块模拟内自然侵蚀已含，Stage2 河流另加）";
}

/// <summary>环8：洋流→降水→洋流（截断）。</summary>
public sealed class CurrentPrecipLoop : ModelBase, ILoopRole
{
    public override string Name => "洋流→降水→洋流";
    public string Status => "Cut";
    public override float Magnitude => 0.25f;
    public void Apply() { }   // 闭环行为：Closed 环后续实现，Cut/Ignored 无操作
    public string Decision => "洋流冷暖修正降水（ClimateGenerator）；降水不回流洋流（表层流主要风驱）。热盐深层环流未建模（表层模型）";
}
