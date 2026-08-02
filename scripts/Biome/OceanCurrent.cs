using Godot;

namespace World.Biome;

/// <summary>
/// 表层洋流（风应力旋度 + 流函数法，Sverdrup 简化版）。
///
/// 物理（2026-08-02 重写，替代"方向=风向"的简化——那个只有东西向条带、无闭合环流）：
///   1. 风应力旋度 curl(τ)：表层风对海洋施加应力，其旋度驱动洋流涡度。
///      用邻居环量近似：curl ≈ ∮ wind·dl（沿顶点邻居多边形积分）。
///   2. 流函数 ψ：解球面泊松方程 ∇²ψ = curl(τ)（Gauss-Seidel 迭代）。
///      ψ 等值线 = 流线 → 自动形成闭合环流（gyre）：
///        信风带（应力向西）→ 副热带反气旋环流（北半球顺时针）
///        西风带 → 气旋环流；大陆边界 ψ=0 → 洋流自动绕行
///   3. 洋流方向 = ψ 梯度旋转 90°（切平面内，∇ψ×r）
///   4. 冷暖 = 洋流经向分量：向极（远离赤道）= 暖流（湾流/巴西流），
///      向赤道 = 寒流（加那利/本格拉）——两个半球统一（沿流线从低纬来）。
///
/// ⚠️ 纯计算（无 Godot 对象依赖），生成时后台线程安全。
/// 需要邻居表（int[][]）——球面拉普拉斯/环量依赖它。
/// </summary>
public static class OceanCurrent
{
    /// <summary>
    /// 计算洋流场（流函数法）。
    /// </summary>
    /// <param name="verts">球面顶点（单位方向）</param>
    /// <param name="neighbors">邻接表（每顶点邻居 id 数组）</param>
    /// <param name="elevNorm">每顶点归一化海拔（&lt;0 = 海洋）</param>
    /// <param name="dirs">输出：洋流方向（单位切向量；内陆 = zero）</param>
    /// <param name="warmth">输出：冷暖 -1（寒流）~ +1（暖流），0 = 内陆</param>
    /// <param name="strength">输出：强度 0.3（纯风驱弱流）~ 1.0（西边界强化强流），0 = 内陆</param>
    /// <param name="iterations">Gauss-Seidel 迭代次数（默认 300，够收敛）</param>
    public static void Compute(
        Vector3[] verts, int[][] neighbors, float[] elevNorm,
        out Vector3[] dirs, out float[] warmth, out float[] strength,
        int iterations = 300)
    {
        int n = verts.Length;
        dirs = new Vector3[n];
        warmth = new float[n];
        strength = new float[n];

        // ── 1. 风应力旋度 curl(τ)：邻居环量近似 ∮ wind·dl ──
        var curl = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (elevNorm[i] >= 0f) continue;   // 只算海洋
            var nbs = neighbors[i];
            if (nbs == null || nbs.Length < 3) continue;
            float c = 0f;
            for (int j = 0; j < nbs.Length; j++)
            {
                int a = nbs[j];
                int b = nbs[(j + 1) % nbs.Length];
                Vector3 va = WindField.WindAt(verts[a]);
                // 边向量（球面切向）：b - a 投影到切平面
                Vector3 edge = (verts[b] - verts[a]);
                c += va.Dot(edge);
            }
            // 归一化：除以环面积（用邻居数近似）
            curl[i] = c / nbs.Length;
        }

        // ── 2. 解 ∇²ψ = curl（Gauss-Seidel 迭代，均匀权重拉普拉斯）──
        //    陆地顶点 ψ = 0（边界条件：洋流沿大陆绕行）
        var psi = new float[n];
        for (int iter = 0; iter < iterations; iter++)
        {
            float maxErr = 0f;
            for (int i = 0; i < n; i++)
            {
                if (elevNorm[i] >= 0f) { psi[i] = 0f; continue; }
                var nbs = neighbors[i];
                if (nbs == null || nbs.Length < 3) continue;
                float sum = 0f;
                foreach (var nb in nbs) sum += psi[nb];
                float next = (sum - curl[i]) / nbs.Length;
                float err = Mathf.Abs(next - psi[i]);
                if (err > maxErr) maxErr = err;
                psi[i] = next;
            }
            if (maxErr < 1e-5f) break;
        }

        // ── 3. 洋流方向 = ψ 等值线切向（纯环流）──
        //    ⚠️ 2026-08-02 v3：去掉风驱混合（d = gyre + windDrift）——
        //      风驱权重固定 1.0 把环流淹没：开阔大洋 |∇ψ|≈0 → dirs≈风向 →
        //      流线成平行直线（堆叠）且不绕圈（不形成环流）。
        //      纯环流方向（∇ψ×r 归一化）：流线沿 ψ 等值线走，自动绕圈闭合（环流成型）。
        //      |∇ψ|≈0（开阔大洋/环流中心）→ dirs=0 → 空白（真实洋流图不铺满）。
        //    球面梯度：Σ (ψ_j - ψ_i) · 单位边方向
        for (int i = 0; i < n; i++)
        {
            if (elevNorm[i] >= 0f) continue;
            var nbs = neighbors[i];
            if (nbs == null || nbs.Length < 3) continue;
            Vector3 grad = Vector3.Zero;
            foreach (var nb in nbs)
            {
                Vector3 e = (verts[nb] - verts[i]);
                float len = e.Length();
                if (len < 1e-9f) continue;
                grad += (psi[nb] - psi[i]) * (e / len);
            }
            // 环流方向 = ψ 梯度旋转 90°（∇ψ × r），归一化（等值线切向）
            Vector3 gyre = grad.Cross(verts[i]);
            // 环流权重：边界/转向带强（|grad| 大），开阔大洋弱
            float gyreW = Mathf.Min(1f, grad.Length() * 8f);
            if (gyre.LengthSquared() > 1e-12f && gyreW > 0.03f)
            {
                dirs[i] = gyre.Normalized();
                // 强度：0.3（弱环流）~ 1.0（西边界强化强流）——2026-08-02 新增，
                //   修正系数动态化的输入（强流带影响大、开阔弱流影响小）
                strength[i] = 0.3f + gyreW * 0.7f;
            }
            // else：|∇ψ|≈0（开阔大洋/环流中心）→ dirs 保持 zero → 流线空白（不铺满）
        }

        // ── 4. 冷暖 = 洋流经向分量（向极 = 暖流，向赤道 = 寒流）──
        //    towardPole：本地经线方向的向极切向量（北半球 +Y，南半球 -Y）
        for (int i = 0; i < n; i++)
        {
            if (elevNorm[i] >= 0f) continue;
            if (dirs[i].LengthSquared() < 1e-12f) continue;
            Vector3 r = verts[i];
            // 经线向极方向 = (r 的经线切线)·sign(lat)
            float lat = Mathf.Asin(Mathf.Clamp(r.Y, -1f, 1f));
            // 向极切向量：r 在 XZ 平面投影归一化 × sign(lat)
            Vector3 toPole = new Vector3(r.X, 0f, r.Z);
            if (toPole.LengthSquared() < 1e-12f) continue;   // 极点无经线方向
            toPole = toPole.Normalized() * (lat >= 0f ? 1f : -1f);
            // 但 toPole 是"向极"的经向分量（含 -Y 北半球分量）
            // 经向单位向量（向极）：
            Vector3 meridianPole = (toPole - r * toPole.Dot(r)).Normalized();
            float poleComp = dirs[i].Dot(meridianPole);
            // 向极分量 1 = 暖流（从赤道来），-1 = 寒流
            warmth[i] = Mathf.Clamp(poleComp * 3f, -1f, 1f);
        }
    }
}
