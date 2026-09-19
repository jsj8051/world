namespace World.NewHexWorld.Plate
{
	// 全局地壳场（设计入口 §3.1）：数组与 Ball.CellIds 对齐，长度 = H3 格数。
	// 由 H3DynamicTectonics 演化后经 WriteToCrust 写定；渲染层只读本场、禁止伪造。
	// 将来要素生成器加起伏时【成对写厚度与海拔】。
	public class Crust
	{
		public int[] PlateId;        // 每格归属板块（铺满全球，无空洞）
		public float[] FelsicThick;  // 长英质厚（m）——陆壳本体
		public float[] MaficThick;   // 镁铁质厚（m）——洋壳本体
		public float[] SedimentThick;// 沉积物厚（m）
		public float[] Age;          // 岩石圈热年龄（My；热沉降的唯一输入）
		public float[] Elevation;    // 海拔（m，0 = 海平面）= 均衡位移 − 海平面
		public float[] TemperatureC; // 年均温（°C）——H3Climate 终态一次计算（纬度+倾角+辐照+噪声+直减）
		public float[] PrecipMmYear; // 年降水（mm/yr）——结构场经 H3WaterCycle λ 闭合（全球 Σ降水=Σ蒸发）

		// 统一判陆口（全工程唯一陆海判定口径）：海拔 > 0 = 露出海面。
		// 海平面按全球水量求解、容器含被淹没的陆壳 ⇒ 陆壳可以低于海平面（大陆架/内陆浅海），
		// "有长英质"不等价于"露出水面"，四家（水循环/气候/渲染/面板）必须共用这一个口。
		// 需要物质口径（是陆壳还是洋壳本体）的地方直接读 FelsicThick[i] > 0，不要借道本方法。
		public bool IsLand(int cellIndex) => Elevation[cellIndex] > 0f;
	}
}
