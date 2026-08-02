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
            default:          towardPole = -1f; break;   // 极地：向赤道（极地下沉外流）
        }
        float mer = north ? towardPole : -towardPole;    // 北/南半球镜像

        // 科里奥利偏转（右手系推导验证）：
        //   运动速度 v = mer×northDir → 科里奥利加速度 a_cor = -2Ω×v
        //   北半球顺转：向极地(mer>0)→东偏（西风带），向赤道(mer<0)→西偏（东北信风）
        //   即 east 分量 = mer × sin|lat| × coriolisHemi
        //   南半球左偏 → 符号翻转；逆转自转（金星式）→ 再翻转
        float coriolisHemi = (north ? 1f : -1f) * (Prograde ? 1f : -1f);
        float deflect = Mathf.Sin(Mathf.Abs(lat));   // 赤道 0 → 极地 1
        Vector3 wind = northDir * mer + east * (mer * deflect * coriolisHemi);

        return wind.LengthSquared() > 1e-9f ? wind.Normalized() : east;
    }

    /// <summary>
    /// 风"从海洋来"的程度：-1 = 纯大陆风（干燥），+1 = 纯海洋风（湿润）。
    /// 需要调用方提供海陆判断（elevNorm &lt; 0 = 海洋）。用风向上的采样判断。
    /// ⚠️ 采样距离必须 > 网格顶点间距（n=16 时 ~7°；n=32 时 ~3.5°）——
    ///   距离太短会落在同一 Voronoi 胞内，海陆判断失效（实测 0.04rad 无效果）。
    /// </summary>
    public static float MaritimeScore(Vector3 pos, float elevNorm, System.Func<Vector3, float> sampleElev)
    {
        Vector3 dir = pos.Normalized();
        Vector3 wind = WindAt(dir);
        // 沿风向（上风向，即 -wind）采样 3 个点，看是否经过海洋
        // 0.10/0.18/0.26 弧度 ≈ 5.7°/10.3°/14.9°：跨过多个顶点胞
        float score = 0f;
        for (int i = 1; i <= 3; i++)
        {
            Vector3 up = (dir - wind * (0.10f * i)).Normalized();   // 上风向
            if (sampleElev(up) < 0f) score += 0.5f;                  // 海洋湿润
            else score -= 0.25f;                                     // 陆地干燥
        }
        return Mathf.Clamp(score, -1f, 1f);
    }
}
