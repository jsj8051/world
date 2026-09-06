using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using World.Utils;
using World.NewHexWorld;

namespace World.NewHexWorld.Plate
{
    public class H3Plate
    {
        public int[] PlateId;
        Ball _ball;                       // 只读引用：Cells 长度 / CellCenters / CellNeighbors
        public float[] Age;               // 洋壳 0–200My、陆壳 ~1000My（B，驱动俯冲）
        public float[] Thick;             // 地壳厚度 m：洋 ~7km、陆 30–70km（B）
        public float[] Elevation;         // 海拔 m（B 输出）

        // —— 板表（并行数组或小 class 随你）——
        int _numPlates;
        Vector3[] _plateOmega;

        public H3Plate(int numPlates, Ball ball)
        {
            _numPlates = numPlates;
            _ball = ball;
            PlateId = new int[ball.CellIds.Length];   // 每格归属板（长度 = 格数；此前未 new → 分板写入即 NRE，2026-09-03 补）
        }

        public void CreatePlates(int seed)
        {
            BuildPlanet(seed);
        }

        private void BuildPlanet(int seed)
        {
            _plateOmega = new Vector3[_numPlates];   // 板角速度表（此前未 new → 赋值即 NRE，2026-09-03 补）

            var seeds = new Vector3[_numPlates];
            for (int i = 0; i < _numPlates; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / _numPlates;          // 纬度均匀分布（±1）
                float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));     // 该纬度的圆半径
                float theta = (float)(Math.PI * (1 + Math.Sqrt(5)) * i);  // 黄金角经度步进
                seeds[i] = new Vector3(Mathf.Cos(theta) * r, y, Mathf.Sin(theta) * r);
            }

            for (int i = 0; i < _ball.CellCenters.Length; i++)
            {
                Vector3 c = _ball.CellCenters[i];
                int best = 0;
                float bestDot = float.MinValue;
                for (int p = 0; p < _numPlates; p++)
                {
                    float d = c.Dot(seeds[p]);
                    if (d > bestDot) { bestDot = d; best = p; }
                }
                PlateId[i] = best;
            }

            // 3. 每板角速度：随机极点（球面均匀采样）+ 转速 0.005–0.02 rad/My
            var rng = new DeterministicRandom(seed);
            for (int plate = 0; plate < _numPlates; plate++)
            {
                // —— 球面均匀采样旋转轴方向（关键：高度分量均匀 → 极点不聚集）——
                float axisHeight = 2f * (float)rng.NextDouble() - 1f;            // 轴的高度分量 ∈ [−1,1]
                float axisLongitude = 2f * Mathf.Pi * (float)rng.NextDouble();   // 轴的经度角 ∈ [0, 2π)
                float axisRadius = Mathf.Sqrt(Mathf.Max(0f, 1f - axisHeight * axisHeight));  // 轴在赤道面的投影半径
                var rotationAxis = new Vector3(
                    Mathf.Cos(axisLongitude) * axisRadius,
                    axisHeight,
                    Mathf.Sin(axisLongitude) * axisRadius);          // 单位向量：这块板绕它转（右手定则）

                // —— 转速：现实板速量级（2–10 cm/yr），且保证单步位移不超半格 ——
                float speed = 0.005f + 0.015f * (float)rng.NextDouble();
                _plateOmega[plate] = rotationAxis * speed;           // 角速度向量 = 旋转轴 × 转速
            }

        }




    }
}
