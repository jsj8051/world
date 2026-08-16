using Godot;
using System;
using System.Collections.Generic;
using World.Services;

namespace World.Tectonics
{
    // 职责：超级大陆循环——碰撞缝合聚合（MergeTwoPlates）+ 继承式裂解（TrySplitSupercontinent）
    //   与软流圈速度重分割（ResetPlates）。
    //   TryMergeCollidingPlates —— 接触边界够长 → 碰撞缝合（印度-欧亚式聚合）
    //   MergeTwoPlates          —— 合并板块 b 进 a（a 坐标系为准）
    //   TrySplitSupercontinent  —— 超大陆（占全球 >25%）时继承式裂解成 2-3 块
    //   ResetPlates             —— 软流圈压力扩散 + 图像分割重分板块（洋壳年龄重置）
    public partial class TectonicsSimulation
    {
        /// <summary>
        /// 碰撞合并（2026-08-16 方案 B 前提）：板块接触边界够长 → 缝合（碰撞合并）。
        /// 真实地球：印度-欧亚碰撞缝合 → 大陆拼大。旧模拟缺此机制（plates 恒 7，
        /// 大陆永远拼不大 → 超大陆永不形成 → 裂解永不触发）。
        /// 用 MergePlatesToMaster 后的 TopPlateMap 检测相邻板块接触长度。
        /// </summary>
        public void TryMergeCollidingPlates()
        {
            // ⚠️ 2026-08-16 v2：缝合下限——真实地球 8 大板块 + 洋中脊持续分裂维持数量。
            //   实测无下限时全部吞并成 1 块（100% 超大陆，失真）；下限 5 保留多板块格局，
            //   与超大陆裂解（→7）形成 5-8 动态平衡。
            if (Plates.Count <= 5) return;
            int n = GlobalGrid.VertexCount;
            int minContact = Math.Max(8, n / 50);   // ≥2% 全球接触才缝合
            var contact = new System.Collections.Generic.Dictionary<(int, int), int>();
            for (int i = 0; i < n; i++)
            {
                int a = (int)TopPlateMap[i];
                if (a < 0) continue;
                foreach (var nb in GlobalGrid.Neighbors[i])
                {
                    int b = (int)TopPlateMap[nb];
                    if (b < 0 || b == a) continue;
                    var key = a < b ? (a, b) : (b, a);
                    contact[key] = contact.GetValueOrDefault(key) + 1;
                }
            }
            int bestA = -1, bestB = -1, bestC = minContact;
            foreach (var kv in contact)
                if (kv.Value > bestC) { bestC = kv.Value; bestA = kv.Key.Item1; bestB = kv.Key.Item2; }
            if (bestA < 0) return;
            MergeTwoPlates(bestA, bestB);
        }

        /// <summary>合并板块 b 进 a（a 坐标系为准；物质从 WorldCrust 全局视图取——先 MergePlatesToMaster）。</summary>
        private void MergeTwoPlates(int idA, int idB)
        {
            int n = GlobalGrid.VertexCount;
            Plate pa = null, pb = null;
            for (int p = 0; p < Plates.Count; p++)
            {
                if (Plates[p].Id == idA) pa = Plates[p];
                if (Plates[p].Id == idB) pb = Plates[p];
            }
            if (pa == null || pb == null) return;

            var merged = new Plate(idA, GlobalGrid, new Crust(GlobalGrid), new byte[n]);
            Array.Copy(pa.LocalToGlobal, merged.LocalToGlobal, 9);
            Array.Copy(pa.GlobalToLocal, merged.GlobalToLocal, 9);
            Array.Copy(pa.LocalIdsOfGlobalCells, merged.LocalIdsOfGlobalCells, n);
            Array.Copy(pa.GlobalIdsOfLocalCells, merged.GlobalIdsOfLocalCells, n);
            Array.Copy(pa.Velocity, merged.Velocity, n);
            var mPools = merged.Crust.AllPools();
            var gPools = WorldCrust.AllPools();
            for (int l = 0; l < n; l++)
            {
                int g = pa.GlobalIdsOfLocalCells[l];
                if (TopPlateMap[g] == idA || TopPlateMap[g] == idB)
                {
                    merged.Mask[l] = 1;
                    for (int q = 0; q < 8; q++) mPools[q][l] = gPools[q][g];
                }
            }
            Plates.Remove(pb);
            for (int p = 0; p < Plates.Count; p++)
                if (Plates[p].Id == idA) { Plates[p] = merged; break; }
            LogService.Log("Tectonics", $"碰撞缝合: 板块{idA}+{idB} → {idA}（板块数→{Plates.Count}）");
        }

        /// <summary>
        /// 方案 B（2026-08-16 用户拍板）：超大陆【继承式裂解】——替代旧固定周期全局洗牌。
        /// 最大板块占全球 &gt;25% 时视为超大陆，沿内部最远点种子裂成 2-3 块；其他板块不动
        /// （位置/演化历史继承），板块漂移碰撞自然聚合拼大 → 大陆大小有自然差异。
        /// 无超大陆返回 false（继续聚合，不洗牌）。
        /// </summary>
        public bool TrySplitSupercontinent()
        {
            int n = GlobalGrid.VertexCount;
            int bestIdx = -1, bestCount = 0;
            for (int p = 0; p < Plates.Count; p++)
            {
                int c = Plates[p].TileCount;
                if (c > bestCount) { bestCount = c; bestIdx = p; }
            }
            float frac = (float)bestCount / n;
            if (bestIdx < 0 || frac < 0.25f) return false;   // 无超大陆 → 继续聚合
            int splitK = frac > 0.4f ? 3 : 2;

            var big = Plates[bestIdx];
            var cells = new System.Collections.Generic.List<int>();
            for (int i = 0; i < n; i++) if (big.Mask[i] == 1) cells.Add(i);
            if (cells.Count < splitK * 2) return false;

            // 最远点种子（球面分散）：种子0 = 离质心最远；其后 = 离已选种子最远
            Vector3 centroid = Vector3.Zero;
            foreach (var c in cells) centroid += GlobalGrid.Vertices[c];
            centroid = centroid.Normalized();
            var seeds = new System.Collections.Generic.List<int>();
            int bestC = cells[0]; float bestD = -1f;
            foreach (var c in cells)
            {
                float d = 1f - GlobalGrid.Vertices[c].Dot(centroid);
                if (d > bestD) { bestD = d; bestC = c; }
            }
            seeds.Add(bestC);
            while (seeds.Count < splitK)
            {
                int bestS = cells[0]; float bestSD = -1f;
                foreach (var c in cells)
                {
                    float minD = float.MaxValue;
                    foreach (var s in seeds)
                    {
                        float d = 1f - GlobalGrid.Vertices[c].Dot(GlobalGrid.Vertices[s]);
                        if (d < minD) minD = d;
                    }
                    if (minD > bestSD) { bestSD = minD; bestS = c; }
                }
                seeds.Add(bestS);
            }

            // 每格归最近种子 → 裂片 mask
            var newMasks = new byte[splitK][];
            for (int k = 0; k < splitK; k++) newMasks[k] = new byte[n];
            foreach (var c in cells)
            {
                int bestS = 0; float bestSD = float.MaxValue;
                for (int k = 0; k < splitK; k++)
                {
                    float d = 1f - GlobalGrid.Vertices[c].Dot(GlobalGrid.Vertices[seeds[k]]);
                    if (d < bestSD) { bestSD = d; bestS = k; }
                }
                newMasks[bestS][c] = 1;
            }

            // 重建 Plates：其他板块保留（演化历史继承），超大陆裂片继承 big 的 crust/坐标系
            var newPlates = new System.Collections.Generic.List<Plate>();
            for (int p = 0; p < Plates.Count; p++)
                if (p != bestIdx) newPlates.Add(Plates[p]);
            int nextId = 0;
            foreach (var pl in Plates) nextId = Math.Max(nextId, pl.Id + 1);
            var bigPools = big.Crust.AllPools();
            for (int k = 0; k < splitK; k++)
            {
                var crust = new Crust(GlobalGrid);
                var cpools = crust.AllPools();
                for (int i = 0; i < n; i++)
                    if (newMasks[k][i] == 1)
                        for (int q = 0; q < 8; q++) cpools[q][i] = bigPools[q][i];
                var plate = new Plate(nextId + k, GlobalGrid, crust, newMasks[k]);
                // 继承 big 的坐标系/速度场（裂片原地分裂，不跳变）
                Array.Copy(big.LocalToGlobal, plate.LocalToGlobal, 9);
                Array.Copy(big.GlobalToLocal, plate.GlobalToLocal, 9);
                Array.Copy(big.LocalIdsOfGlobalCells, plate.LocalIdsOfGlobalCells, n);
                Array.Copy(big.GlobalIdsOfLocalCells, plate.GlobalIdsOfLocalCells, n);
                Array.Copy(big.Velocity, plate.Velocity, n);
                newPlates.Add(plate);
            }
            Plates = newPlates;
            LogService.Log("Tectonics", $"超大陆裂解: 占比{frac:P0} → {splitK}块（板块数→{Plates.Count}）");
            return true;
        }

        public void ResetPlates(int numPlates, int minSegmentSize = 200)
        {
            int n = GlobalGrid.VertexCount;

            // 1. 软流圈压力场：pressure = -buoyancy，然后拉普拉斯扩散（全场连续）
            //    对应 FluidMechanics.get_fluid_pressures：细网格 3 次 + 粗网格 30 次 + 细 3 次。
            //    两级扩散：粗网格（n=8，642 格）建立大尺度涡旋结构，细网格保留边界细节。
            //    ⚠️ 单级细网格扩散：5 次太毛糙（碎片）、30 次过度平滑（单一涡旋），
            //    都分割失败。粗细两级是原版设计（2026-08-02 验证）。
            var thickness = WorldCrust.GetThickness(Material);
            var mass = WorldCrust.GetTotalMass();
            var density = WorldCrust.GetDensity(mass, thickness, Material.MaficVolcanicMin);
            var buoyancy = WorldCrust.GetBuoyancy(density, Material, SurfaceGravity);

            var pressure = new float[n];
            for (int i = 0; i < n; i++) pressure[i] = -buoyancy[i];

            // 细网格 3 次（去局部噪声）
            pressure = FieldOps.Diffuse(GlobalGrid, pressure, 0.5f, 3);
            // 粗网格（n=8）30 次：建立大尺度结构
            var coarse = new SphereGrid(8);
            // 压力场重采样到粗网格（最近邻）
            var coarseIds = new int[coarse.VertexCount];
            for (int i = 0; i < coarse.VertexCount; i++)
                coarseIds[i] = GlobalGrid.NearestId(coarse.Vertices[i]);
            var coarsePressure = new float[coarse.VertexCount];
            for (int i = 0; i < coarse.VertexCount; i++)
                coarsePressure[i] = pressure[coarseIds[i]];
            coarsePressure = FieldOps.Diffuse(coarse, coarsePressure, 0.5f, 30);
            // 重采样回细网格
            for (int i = 0; i < n; i++)
                pressure[i] = coarsePressure[coarse.NearestId(GlobalGrid.Vertices[i])];
            // 细网格再 3 次（平滑重采样的台阶）
            pressure = FieldOps.Diffuse(GlobalGrid, pressure, 0.5f, 3);

            // 2. 软流圈速度 = 压力梯度（对应 get_fluid_velocities）
            var velocity = new Vector3[n];
            FieldOps.Gradient(GlobalGrid, pressure, velocity);

            // 3. 角速度 = v × pos（切向分量 = 旋转）
            var angular = new Vector3[n];
            Tectonophysics.CrossToAngularVelocity(velocity, GlobalGrid.Vertices, angular);

            // 诊断：角速度场统计
            float maxAng = 0, avgAng = 0, nzCount = 0, dirSpread = 0;
            Vector3 dirSum = Vector3.Zero;
            for (int i = 0; i < n; i++)
            {
                float len = angular[i].Length();
                if (len > 0) { avgAng += len; nzCount++; dirSum += angular[i] / len; }
                if (len > maxAng) maxAng = len;
            }
            if (nzCount > 0) { avgAng /= nzCount; dirSpread = dirSum.Length() / nzCount; }
            LogService.Log("Tectonics", $"角速度场: max={maxAng:E2} avg={avgAng:E2} 非零格={nzCount}/{n} 方向一致性={dirSpread:F2}(1=全同向)");

            // 3. 图像分割
            var plateMap = Tectonophysics.GuessPlateMap(GlobalGrid, angular, numPlates, minSegmentSize);

            // 4. 重建板块（继承 WorldCrust）
            // ⚠️ 2026-08-03 板块数收敛根因（控制变量实验 ResetDiag 结论）：
            //   "洋壳年龄饱和"不是主因（v1 只改年龄：饱和率 92%→57% 板块数轨迹不变；
            //   298My 未重置 94% 饱和的场反而分割出 6 块）——主因是**请求棘轮**：
            //   旧实现 ResetPlates(Mathf.Max(Plates.Count,4)) 请求数每周期只降不升，
            //   分割结果 ≤ 请求数 → 单调掉块（8→7→6→5→4→3，900My 固定 2-3 块）。
            //   请求恒 8 时 592My 侵蚀开仍分割出 7 块（ResetDiag 实验3）。
            //   修复：请求数固定 = 初始板块数（RunWithProgress 已改）。
            //   年龄重置（本函数）：超级大陆裂解 = 威尔逊旋回，纯洋壳格重新注入 0~200My
            //   年龄梯度（与初始地壳同结构低频噪声）——洋壳不再只老不新（原版无重置时
            //   600My 全饱和 → 海底过深过均匀），恢复密度差驱动的物理。大陆/造山带 age 不动
            //   （felsic 密度固定，age 只影响 mafic）。
            Plates.Clear();
            var plateIds = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < n; i++) if (plateMap[i] > 0) plateIds.Add(plateMap[i]);

            var ageNoise1 = new FastNoiseLite();
            ageNoise1.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
            ageNoise1.Frequency = NumContinents / (2f * Mathf.Pi);   // 同 GenerateInitialCrust 主尺度（N 波长/球）
            ageNoise1.Seed = Seed;
            var ageNoise2 = new FastNoiseLite();
            ageNoise2.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
            ageNoise2.Frequency = ageNoise1.Frequency * 1.625f;      // 次尺度（同比例）
            ageNoise2.Seed = Seed + 100;

            var wPools = WorldCrust.AllPools();
            foreach (int pid in plateIds)
            {
                var mask = new byte[n];
                var crust = new Crust(GlobalGrid);
                var cPools = crust.AllPools();
                for (int i = 0; i < n; i++)
                {
                    if (plateMap[i] != pid) continue;
                    mask[i] = 1;
                    for (int k = 0; k < 8; k++) cPools[k][i] = wPools[k][i];
                    // 洋壳年龄重置：mafic 为主的格（排除大陆核心 felsic≥1e7）
                    float felsic = wPools[3][i] + wPools[4][i];
                    if (wPools[5][i] > 1e6f && felsic < 1e7f)
                    {
                        Vector3 p = GlobalGrid.Vertices[i];   // 单位球坐标（噪声频率标定，同 N/2π）
                        float t = 0.5f * (ageNoise1.GetNoise3D(p.X, p.Y, p.Z) * 0.5f + 0.5f)
                                + 0.5f * (ageNoise2.GetNoise3D(p.X, p.Y, p.Z) * 0.5f + 0.5f);
                        crust.Age[i] = Mathf.Clamp(t, 0f, 1f) * 200f * Units.MEGAYEAR;
                    }
                }
                Plates.Add(new Plate(pid, GlobalGrid, crust, mask));
            }
            LogService.Log("Tectonics", $"超级大陆循环: 重新分割为 {Plates.Count} 块板（洋壳年龄已重置）");
        }
    }
}
