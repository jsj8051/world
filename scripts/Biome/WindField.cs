using Godot;

namespace World.Biome;

/// <summary>
/// 星球盛行风场（球面，三圈环流 + 科里奥利偏转）。
///
/// 模型（简化到策略游戏精度，基于行星气候学）：
///   - 三圈环流：Hadley(0-30°) / Ferrel(30-60°) / 极地(60-90°)
///   - 地面风向：
///       Hadley：向赤道方向（北半球向南、南半球向北）+ 科里奥利偏转
///       Ferrel：向极地方向 + 偏转（中纬西风）
///       极地：向赤道方向 + 偏转（极地东风）
///   - 科里奥利：顺转自转（自西向东，地球式）→ 北半球气流右偏、南半球左偏；
///     逆转自转 → 全部镜像（金星式反向环流）。
///
/// 输出：WindAt(pos) → 球面切向单位向量（风向）+ 风速分量。
/// 纯查询接口（无状态、线程安全），供气候/降水/生物群系使用。
/// </summary>
public static class WindField
{
    /// <summary>自转方向（true=顺转，地球式自西向东；false=逆转，金星式）。</summary>
    public static bool Prograde = true;

    /// <summary>自转速度（相对地球 24h = 1.0）。科里奥利力 ∝ Ω：
    /// 0.2（~5 天/圈，慢速）→ 偏转弱 → 风带模糊、全球均一（金星式）；
    /// 1.0（地球）→ 标准信风/西风带；5（~5h/圈，快速）→ 偏转强 → 纬向急流、风带分明。
    /// ⚠️ 2026-08-02：科里奥利偏转强度 = sin|lat| × speed^0.7（亚线性——气候响应
    ///   对 Ω 是非线性的，地球 1.0 标定不变）。</summary>
    public static float RotationSpeed = 1.0f;

    /// <summary>
    /// 纬度 → 环流带类型（按 |lat|）。
    /// </summary>
    public enum Belt { Hadley, Ferrel, Polar }

    public static Belt BeltAt(float latDeg)
    {
        float a = Mathf.Abs(latDeg);
        if (a < 30f) return Belt.Hadley;
        if (a < 60f) return Belt.Ferrel;
        return Belt.Polar;
    }

    /// <summary>
    /// 球面点 → 盛行风向（切向单位向量，指向下风向）。
    /// pos 单位方向；返回的向量 ⊥ pos（切平面）。
    /// 顺转自转：北半球风向右偏 → 信风为东北风（向东+向赤道）。
    /// </summary>
    public static Vector3 WindAt(Vector3 pos)
    {
        Vector3 dir = pos.Normalized();
        float lat = Mathf.Asin(Mathf.Clamp(dir.Y, -1f, 1f));
        float latDeg = Mathf.RadToDeg(lat);
        bool north = lat >= 0f;
        float a = Mathf.Abs(latDeg);
        Belt belt = BeltAt(a);

        // 切向基：东（E）= 经度增加方向，北（N）= 纬度增加方向
        // E = up × dir（在赤道面内，指向东）；N = dir × E（指向北）
        Vector3 east = new Vector3(-dir.Z, 0f, dir.X).Normalized();
        Vector3 northDir = dir.Cross(east).Normalized();

        // 环流水平分量（指向：Hadley→赤道，Ferrel→极地，Polar→赤道）
        // 垂直分量（上升/下沉）由辐合/辐散决定，这里简化只做水平风
        float towardPole;   // +1 = 向极地，-1 = 向赤道
        switch (belt)
        {
            case Belt.Hadley: towardPole = -1f; break;   // 向赤道（赤道辐合上升）
            case Belt.Ferrel: towardPole = 1f; break;    // 向极地（极锋辐合）
            default: towardPole = -1f; break;   // 极地：向赤道（极地下沉外流）
        }
        float mer = north ? towardPole : -towardPole;    // 北/南半球镜像

        // 科里奥利偏转（右手系推导验证）：
        //   运动速度 v = mer×northDir → 科里奥利加速度 a_cor = -2Ω×v
        //   北半球顺转：向极地(mer>0)→东偏（西风带），向赤道(mer<0)→西偏（东北信风）
        //   即 east 分量 = mer × sin|lat| × coriolisHemi
        //   南半球左偏 → 符号翻转；逆转自转（金星式）→ 再翻转
        float coriolisHemi = (north ? 1f : -1f) * (Prograde ? 1f : -1f);
        // 偏转强度 = sin|lat| × 自转速度^0.7（科里奥利 ∝ Ω，亚线性标定）
        float deflect = Mathf.Sin(Mathf.Abs(lat)) * Mathf.Pow(RotationSpeed, 0.7f);
        Vector3 wind = northDir * mer + east * (mer * deflect * coriolisHemi);

        return wind.LengthSquared() > 1e-9f ? wind.Normalized() : east;
    }

    /// <summary>
    /// 风"从海洋来"的程度：-1 = 纯大陆风（干燥），+1 = 纯海洋风（湿润）。
    /// 需要调用方提供海陆判断（elevNorm &lt; 0 = 海洋）。
    ///
    /// ⚠️ 2026-08-02 v3：连续指数衰减模型（替代 3 点布尔 → 4 离散档位的阈值型）。
    ///   物理：气团从海洋携带水汽，每走一段路降水损失固定比例 → exp(-d/L) 衰减。
    ///   沿上风向采 10 点，海洋贡献按距离衰减加权：
    ///     快自转 → L 大（水汽输送远、深入内陆，海陆差异小）
    ///     慢自转 → L 小（水汽近岸耗尽，海岸湿内陆干，海陆差异大）
    ///   结果连续 0~1，风向渐变 → 湿润度连续响应（不再需要扫过海岸线才突变）。
    /// </summary>
    public static float MaritimeScore(Vector3 pos, float elevNorm, System.Func<Vector3, float> sampleElev)
    {
        Vector3 dir = pos.Normalized();
        Vector3 wind = WindAt(dir);
        // 水汽衰减尺度：地球 1× 时 L≈0.15 rad（~1000km 内贡献 37%）；∝ speed^0.5
        //   0.2× → 0.067（~450km 就耗尽）；5× → 0.335（~2200km 仍有效）
        float L = 0.15f * Mathf.Pow(RotationSpeed, 0.5f);
        // 采样范围 = 3L（覆盖到贡献可忽略处），最远 0.3~1.0 rad；至少 0.25 rad 跨过顶点胞
        float range = Mathf.Clamp(3f * L, 0.25f, 0.9f);
        const int M = 10;
        float sumW = 0f, sumOcean = 0f;
        for (int i = 1; i <= M; i++)
        {
            float d = range * i / M;
            Vector3 up = (dir - wind * d).Normalized();   // 上风向
            float w = Mathf.Exp(-d / L);
            sumW += w;
            if (sampleElev(up) < 0f) sumOcean += w;        // 海洋点贡献权重
        }
        float score = sumW > 1e-9f ? sumOcean / sumW : 0f;  // 0~1 连续
        return (score - 0.5f) * 2f;                         // -1（全陆）~ +1（全海）
    }
}
