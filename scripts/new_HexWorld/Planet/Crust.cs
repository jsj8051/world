namespace World.NewHexWorld.Plate
{
	// 全局地壳场（设计入口 §3.1）：数组与 Ball.CellIds 对齐，长度 = H3 格数。
	// 由 H3Plate.CreatePlates 静态生成一次写定，之后只读；本阶段无演化步进。
	// 将来要素生成器加起伏时【成对写厚度与海拔】，渲染层只读本场、禁止伪造。
	public class Crust
	{
		public int[] PlateId;        // 每格归属板块（静态生成铺满全球，无空洞）
		public float[] FelsicThick;  // 长英质厚（m）——陆壳本体（陆性板 35000，洋性板 0）
		public float[] MaficThick;   // 镁铁质厚（m）——洋壳本体（洋性板 7000，陆性板 0）
		public float[] SedimentThick;// 沉积物厚（m）——暂不使用，全 0
		public float[] Age;          // 年龄（My）占位语义：陆格 1000（标记）、洋格 0；无演化
		public float[] Elevation;    // 海拔（m，0=海平面）——静态初值：陆板 +LandElevM、洋板 −OceanDepthM

		// 统一判陆口（全工程唯一陆海判定口径）：长英质厚 > 0 = 陆壳（陆性板写 35000、洋性板恒 0
		// → 二元跳变无中间态）。海拔只是派生初值，勿用 Elevation 正负另立口径（初值两级时碰巧等价，
		// 将来要素生成器加连续起伏必分叉）。显示模式（海陆/海拔/…）与格信息一律走这里。
		public bool IsLand(int cellIndex) => FelsicThick[cellIndex] > 0f;
	}
}
