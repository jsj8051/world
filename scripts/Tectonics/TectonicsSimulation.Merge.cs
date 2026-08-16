using System;

namespace World.Tectonics
{
    // 职责：合并各板块到全局（Lithosphere.merge_plates_to_master 移植）。
    //   MergePlatesToMaster —— 密度最小板在顶层、felsic 守恒叠加，维护 TopPlateMap / PlateCount，
    //    并在收尾按 70km 厚度上限做简化俯冲削减。
    public partial class TectonicsSimulation
    {
        /// <summary>
        /// 合并各板块到全局（Lithosphere.merge_plates_to_master 移植，M2 简化）。
        /// 规则：密度最小的板在顶层（浮力大的覆盖），felsic 类守恒叠加（碰撞增厚），
        /// mafic/age 由顶层板决定。同时维护 PlateCount（每格覆盖板块数，裂谷/俯冲用）。
        /// 简化俯冲（2026-08-02）：叠加后每格地壳总厚度设上限（70km），超出部分
        /// 按比例削减——模拟俯冲带走多余地壳。无此限制碰撞处地壳无限堆叠
        /// （maxDisp 6923→11790m，land 40%→9.5% 崩盘）。
        /// </summary>
        public void MergePlatesToMaster()
        {
            int n = GlobalGrid.VertexCount;
            WorldCrust.Reset();
            Array.Fill(TopPlateMap, -1f);
            Array.Clear(PlateCount, 0, n);

            // ⚠️ GC 优化 C3（2026-08-02）：masterDensity 字段复用（每 merge 160KB × 300 步）
            if (_mergeMasterDensity == null) _mergeMasterDensity = new float[n];
            var masterDensity = _mergeMasterDensity;
            Array.Fill(masterDensity, float.MaxValue);

            // ⚠️ GC 优化 C2（2026-08-02）：原每步 3 次 new Crust（8 数组 × 40962 ≈ 1.3MB）——
            //   300 步 × 3 = 1.2GB 分配 → 老年代 GC 压力随时间涨（Merge 21.6→31.7s/段）。
            //   复用字段（ResampleCrustToGlobal 每次全量覆盖，无残留）。
            if (_mergeGlobalizedCrust == null) _mergeGlobalizedCrust = new Crust(GlobalGrid);
            var globalizedCrust = _mergeGlobalizedCrust;

            foreach (var plate in Plates)
            {
                // 重采样板 crust 到全局
                plate.ResampleCrustToGlobal(globalizedCrust);
                // 板 mask 全局化（局部 mask → 全局）
                if (_mergeGlobalMask == null) _mergeGlobalMask = new byte[n];   // GC 优化 C：复用
                var globalMask = _mergeGlobalMask;
                for (int i = 0; i < n; i++)
                    globalMask[i] = plate.Mask[plate.LocalIdsOfGlobalCells[i]];

                // ⚠️ GC 优化 C4（2026-08-02）：plateDensity 系列复用（GetTotalMass/Thickness/
                //   Density 原每 plate 各 new 160KB → 每步 7×3 = 3.4MB × 300 步 ≈ 1GB）
                if (_mergeMassBuf == null) _mergeMassBuf = new float[n];
                if (_mergeThickBuf == null) _mergeThickBuf = new float[n];
                if (_mergeDensityBuf == null) _mergeDensityBuf = new float[n];
                var plateDensity = plate.Crust.GetDensity(
                    plate.Crust.GetTotalMass(_mergeMassBuf),
                    plate.Crust.GetThickness(Material, _mergeThickBuf),
                    Material.MaficVolcanicMin, _mergeDensityBuf);

                // ⚠️ GC 优化 C（2026-08-02）：ConservedPools 每次 new 5 元素引用数组——
                //   内循环 40962 次 × 2 调用 = 8 万次小分配/merge × 3 merge/步 = GC 风暴。
                //   移出循环（每 plate 一次）。
                var c1 = WorldCrust.ConservedPools();
                var c2 = globalizedCrust.ConservedPools();
                for (int i = 0; i < n; i++)
                {
                    if (globalMask[i] == 0) continue;
                    PlateCount[i]++;   // M3-2：覆盖板块数 +1
                    bool onTop = plateDensity[plate.LocalIdsOfGlobalCells[i]] < masterDensity[i];
                    // 守恒组叠加（felsic 类，碰撞增厚）
                    for (int k = 0; k < 5; k++) c1[k][i] += c2[k][i];
                    if (onTop)
                    {
                        masterDensity[i] = plateDensity[plate.LocalIdsOfGlobalCells[i]];
                        TopPlateMap[i] = plate.Id;
                        WorldCrust.MaficVolcanic[i] = globalizedCrust.MaficVolcanic[i];
                        WorldCrust.MaficPlutonic[i] = globalizedCrust.MaficPlutonic[i];
                        WorldCrust.Age[i] = globalizedCrust.Age[i];
                    }
                }
            }

            // 简化俯冲：每格地壳厚度上限（70km，地球大陆根 ~70km），超出削减。
            // ⚠️ GetThickness 返回米（kg/m² ÷ kg/m³ = m）——上限必须 70000 而非 70，
            //   否则全图地壳被削减到 70m（disp[4,11] 崩盘，2026-08-02 修复）。
            float maxThickness = 70000f;   // 70km，单位米
            var thickness = WorldCrust.GetThickness(Material);
            for (int i = 0; i < n; i++)
            {
                if (thickness[i] <= maxThickness) continue;
                float scale = maxThickness / thickness[i];
                var pools = WorldCrust.AllPools();
                for (int k = 0; k < 8; k++) pools[k][i] *= scale;
            }
        }
    }
}
