namespace World.Tectonics
{
    // 职责：离散边界（裂谷）与汇聚边界（俯冲）的板块演化（M3-2 / M3-3）。
    //   UpdateRifting  —— 裂谷生成新洋壳（洋中脊）+ 大陆裂谷带 felsic 减薄（地堑盆地）
    //   UpdateSubducted—— 被压板块消减：深埋变质 + 密度>地幔格移回地幔
    //   ApplyAccretion —— 把 Accretion（增生楔）加到顶层板（俯冲上盘造山带）
    public partial class TectonicsSimulation
    {
        /// <summary>
        /// 裂谷（Lithosphere.update_rifting 移植，M3-2）：离散边界生成新洋壳。
        ///
        /// 原理：板块分离时露出的空当（plate_count==0）或单板边缘（count==1 且顶层=本板）
        /// 就是洋中脊位置。把这些格判为"可裂谷"，重采样到局部后：
        ///   1. 腐蚀 1 层（will_stay_riftable：裂谷区内部，避免边缘误判）
        ///   2. 与板块 mask 外扩 1 层（margin）取交集 → 真正的裂谷格
        ///   3. 这些格填入新洋壳（rifting_crust：mafic_volcanic 7100m，白垩纪洋壳）
        ///   4. 板块 mask 扩展到这些格
        ///
        /// 新洋壳 age=0（年轻），密度低（2890）→ 洋中脊隆起 → 洋底有地形。
        /// 这是洋壳"再循环"的来源：裂谷生成新洋壳 + 俯冲回收老洋壳。
        /// </summary>
        public void UpdateRifting()
        {
            int n = GlobalGrid.VertexCount;
            const float RiftMafic = 7100f * 2890f;   // mafic_volcanic 质量面密度（kg/m²）

            // 全局：is_riftable = (count==0) || (count==1 && top==i)
            // ⚠️ 2026-08-02：并行版已回滚——n=64 实测更慢（82s/段 vs 33s）+ 模拟结果改变
            //   （湖 62→1484）——并行体与串行语义不等价（FieldOps 内部状态/竞态待查），
            //   性能优先方案改用 GC 复用（C3/C4/Erode）。
            foreach (var plate in Plates)
            {
                // 全局可裂谷掩码（对每块板独立判断）
                var globalRiftable = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    if (PlateCount[i] == 0) globalRiftable[i] = 1;
                    else if (PlateCount[i] == 1 && (int)TopPlateMap[i] == plate.Id) globalRiftable[i] = 1;
                }

                // 重采样到局部：全局格 → 板局部最近顶点
                var localRiftable = new byte[LocalGridCount];
                for (int i = 0; i < n; i++)
                    if (globalRiftable[i] == 1)
                        localRiftable[plate.LocalIdsOfGlobalCells[i]] = 1;

                // 腐蚀 1 层（will_stay_riftable：裂谷区内部）
                var willStay = FieldOps.Erode(GlobalGrid, localRiftable, 1);

                // 板块 mask 外扩 1 层（just_outside_border）
                var justOutside = FieldOps.Margin(GlobalGrid, plate.Mask, 1);

                // 裂谷格 = will_stay ∩ just_outside_border
                var isRifting = new byte[LocalGridCount];
                for (int i = 0; i < LocalGridCount; i++)
                    isRifting[i] = (willStay[i] == 1 && justOutside[i] == 1) ? (byte)1 : (byte)0;

                // 填入新洋壳 + 扩展 mask
                for (int i = 0; i < LocalGridCount; i++)
                {
                    if (isRifting[i] == 0) continue;
                    plate.Mask[i] = 1;
                    // ⚠️ 2026-08-03 矿藏模拟化：裂谷新洋壳 = 洋中脊热液活动（铜硫化物矿化）
                    int gi = plate.GlobalIdsOfLocalCells[i];
                    if (gi >= 0 && gi < n) MineralHydro[gi] += 1f;
                    plate.Crust.MaficVolcanic[i] = RiftMafic;
                    plate.Crust.MaficPlutonic[i] = 0;
                    plate.Crust.Sediment[i] = 0;
                    plate.Crust.Sedimentary[i] = 0;
                    plate.Crust.Metamorphic[i] = 0;
                    plate.Crust.FelsicPlutonic[i] = 0;
                    plate.Crust.FelsicVolcanic[i] = 0;
                    plate.Crust.Age[i] = 0;   // 新洋壳年轻
                }
                // ⚠️ 2026-08-18 大陆裂谷（用户拍板 C：B 地堑盆地——东非裂谷型）：
                //   大陆内部张裂带（低频结构——大陆尺度波长）→ felsic 减薄（地堑沉降）
                //   不填洋壳（大陆裂谷不转海洋——死海/东非型：裂谷湖）
                if (_continentalRiftMask != null)
                {
                    for (int i = 0; i < LocalGridCount; i++)
                    {
                        if (plate.Mask[i] == 0) continue;
                        int gi2 = plate.GlobalIdsOfLocalCells[i];
                        if (gi2 < 0 || gi2 >= n || _continentalRiftMask[gi2] != 1) continue;
                        plate.Crust.FelsicPlutonic[i] *= 0.985f;    // 张裂减薄（地堑沉降）
                        plate.Crust.FelsicVolcanic[i] *= 0.985f;
                        plate.Crust.Sedimentary[i] += 5f;            // 裂谷沉积（盆地充填）
                    }
                }
            }
        }

        /// <summary>
        /// 俯冲（Lithosphere.update_subducted 移植，M3-3）：
        /// 被压在下面的板块（count>1 且非顶层）在俯冲带处消减：
        ///   1. 被覆盖格：sediment/sedimentary/felsic → 全部转 metamorphic（深埋变质）
        ///   2. 密度 > 地幔 的板内边界格：从板块 mask 移除（消减回地幔）
        ///   3. 移除格的守恒质量 → 全局 Accretion（增生楔），由 ApplyAccretion
        ///      加到顶层板（俯冲上盘的增生造山带）。
        /// ⚠️ 2026-08-02 修复：旧版把消减质量加回【本板】再 mask=0 → 质量凭空消失
        ///   （全球 felsic 流失 → 600My 后地形磨平到 641m）。原版写入全局 accretion。
        /// </summary>
        public void UpdateSubducted()
        {
            int n = GlobalGrid.VertexCount;
            Accretion.Reset();
            foreach (var plate in Plates)
            {
                // 全局：被俯冲 = count>1 且 非顶层
                var globalSubducted = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    if (PlateCount[i] > 1 && (int)TopPlateMap[i] != plate.Id)
                        globalSubducted[i] = 1;
                }
                // 重采样到局部
                var localSubducted = new byte[LocalGridCount];
                for (int i = 0; i < n; i++)
                    if (globalSubducted[i] == 1)
                        localSubducted[plate.LocalIdsOfGlobalCells[i]] = 1;

                // 深埋变质：被覆盖格的 felsic 全转 metamorphic
                for (int i = 0; i < LocalGridCount; i++)
                {
                    if (localSubducted[i] == 0) continue;
                    // ⚠️ 2026-08-03 矿藏模拟化：俯冲带深埋 = 变质矿化（宝石/铁前体）+ 岩浆弧热液
                    int gi = plate.GlobalIdsOfLocalCells[i];
                    if (gi >= 0 && gi < n)
                    {
                        MineralMeta[gi] += 1f;
                        MineralHydro[gi] += 0.5f;   // 岩浆弧热液（俯冲上盘，金/铜）
                    }
                    plate.Crust.Metamorphic[i] += plate.Crust.Sediment[i] + plate.Crust.Sedimentary[i]
                        + plate.Crust.FelsicPlutonic[i] + plate.Crust.FelsicVolcanic[i];
                    plate.Crust.Sediment[i] = 0;
                    plate.Crust.Sedimentary[i] = 0;
                    plate.Crust.FelsicPlutonic[i] = 0;
                    plate.Crust.FelsicVolcanic[i] = 0;
                }

                // 消减：腐蚀1层 & mask 内扩1层 & 密度>mantle → 从板移除（回地幔）
                var willStay = FieldOps.Erode(GlobalGrid, localSubducted, 1);
                var justInside = FieldOps.Margin(GlobalGrid, plate.Mask, 1);   // 板内边界层
                var density = plate.Crust.GetDensity(
                    plate.Crust.GetTotalMass(), plate.Crust.GetThickness(Material), Material.MaficVolcanicMin);
                for (int i = 0; i < LocalGridCount; i++)
                {
                    if (willStay[i] == 1 && justInside[i] == 1 && density[i] > Material.Mantle)
                    {
                        // 消减：守恒质量 → 全局增生楔（felsic 85/15）
                        float conserved = plate.Crust.Sedimentary[i] + plate.Crust.Metamorphic[i]
                                        + plate.Crust.FelsicPlutonic[i] + plate.Crust.FelsicVolcanic[i];
                        // 全局格 id（本板局部格 → 全局格）
                        int gi = plate.GlobalIdsOfLocalCells[i];
                        Accretion.FelsicPlutonic[gi] += conserved * 0.85f;
                        Accretion.FelsicVolcanic[gi] += conserved * 0.15f;
                        // 本板该格清空（质量已转移到 accretion）
                        plate.Crust.Sedimentary[i] = 0;
                        plate.Crust.Metamorphic[i] = 0;
                        plate.Crust.FelsicPlutonic[i] = 0;
                        plate.Crust.FelsicVolcanic[i] = 0;
                        plate.Mask[i] = 0;
                    }
                }
            }
        }

        /// <summary>
        /// 应用增生楔：把 Accretion 加到顶层板（俯冲上盘增生造山带）。
        /// 对应原版 integrate_deltas 对 accretion 的处理（按 top_plate_map 分派）。
        /// 在 Merge 之后调用（需要 TopPlateMap）。
        /// </summary>
        public void ApplyAccretion()
        {
            int n = GlobalGrid.VertexCount;
            var aPools = Accretion.AllPools();
            for (int p = 0; p < Plates.Count; p++)
            {
                var plate = Plates[p];
                var pPools = plate.Crust.AllPools();
                for (int i = 0; i < n; i++)
                {
                    if ((int)TopPlateMap[i] != p) continue;
                    int li = plate.LocalIdsOfGlobalCells[i];
                    // ⚠️ 2026-08-03 矿藏模拟化：增生楔 = 造山带热液（金/铜）
                    if (aPools[1][i] > 0f || aPools[2][i] > 0f)
                        MineralHydro[i] += 1f;
                    for (int k = 0; k < 8; k++)
                        pPools[k][li] += aPools[k][i];
                }
            }
            Accretion.Reset();
        }
    }
}
