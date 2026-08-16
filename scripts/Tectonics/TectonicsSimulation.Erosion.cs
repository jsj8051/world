using Godot;

namespace World.Tectonics
{
    // 职责：地表过程（Crust.js model_* 移植）——侵蚀/风化/成岩/变质（M3）。
    //   ApplySurfaceProcesses —— 生成 delta 净变化量并应用到 WorldCrust（含矿藏事件累积）
    //   SyncWorldToPlates     —— 把 delta 写回各板块局部 crust（原版 integrate_deltas）
    public partial class TectonicsSimulation
    {
        /// <summary>
        /// 地表过程（M3，Crust.js model_* 移植）：侵蚀/风化/成岩/变质。
        /// 原版流程：calculate_deltas（算 delta 场）→ integrate_deltas（按 top_plate 应用回板块）。
        /// 返回 delta（净变化量），由 SyncWorldToPlates(delta) 写回各板块。
        ///
        /// ⚠️ 2026-08-02 修复：旧版把整个 WorldCrust 覆盖回板块 → 碰撞处叠加的
        ///   felsic（2×）被写回板块 → 下次 Merge 再叠加 → 每步 +1× 无限增长
        ///   （有侵蚀 maxDisp 暴涨到 10642m）。原版只写回 delta（净变化量）。
        /// </summary>
        public Crust ApplySurfaceProcesses(float stepMy)
        {
            int n = GlobalGrid.VertexCount;
            float seconds = stepMy * Units.MEGAYEAR;   // My → 秒

            // 表面高度（相对海平面，≥0）——⚠️ 2026-08-02 性能：字段复用（每步 new 1.3MB × 300 步）
            if (_erodeSurface == null) _erodeSurface = new float[n];
            var surfaceHeight = _erodeSurface;
            for (int i = 0; i < n; i++)
                surfaceHeight[i] = Mathf.Max(Displacement[i] - SeaLevel, 0f);

            // 侵蚀（先生成 delta）——⚠️ 2026-08-02 性能：字段复用 Crust（8 数组 × n）
            if (_erodeDeltaReusable == null) _erodeDeltaReusable = new Crust(GlobalGrid);
            var delta = _erodeDeltaReusable;
            delta.Reset();
            Crust.ModelErosion(GlobalGrid, surfaceHeight, seconds, Material, WorldCrust, delta, ErosionScale);
            Crust.ModelWeathering(GlobalGrid, surfaceHeight, seconds, Material, WorldCrust, delta, ErosionScale);
            Crust.ModelLithification(surfaceHeight, seconds, Material, WorldCrust, delta);
            Crust.ModelMetamorphosis(surfaceHeight, seconds, Material, WorldCrust, delta);

            // ⚠️ 2026-08-03 矿藏模拟化：沉积增厚/变质累积 = 矿化事件
            var dPools = delta.AllPools();   // 0=Sediment 1=Sedimentary 2=Metamorphic
            var dSed = dPools[1];
            var dMeta = dPools[2];
            for (int i = 0; i < n; i++)
            {
                if (dSed[i] > 0f) MineralSed[i] += 1f;    // 沉积盆地（煤/盐前体）
                if (dMeta[i] > 0f) MineralMeta[i] += 1f;  // 变质矿化（宝石/铁）
            }

            // 应用 delta（质量守恒：侵蚀/风化把 felsic 转 sediment，成岩/变质反向）
            Crust.AddDelta(WorldCrust, delta);
            return delta;
        }

        /// <summary>
        /// 把地表过程 delta 写回各板块局部 crust（原版 integrate_deltas 移植）。
        /// 只写回**净变化量**，不覆盖整块 crust——避免碰撞叠加物被写回后无限累积。
        /// </summary>
        public void SyncWorldToPlates(Crust delta)
        {
            int n = GlobalGrid.VertexCount;
            var dPools = delta.AllPools();
            for (int p = 0; p < Plates.Count; p++)
            {
                var plate = Plates[p];
                var pPools = plate.Crust.AllPools();
                for (int i = 0; i < n; i++)
                {
                    if ((int)TopPlateMap[i] != p) continue;
                    int li = plate.LocalIdsOfGlobalCells[i];   // 全局格 → 板局部格
                    for (int k = 0; k < 8; k++)
                        pPools[k][li] += dPools[k][i];
                }
            }
        }
    }
}
