namespace World.Domain
{
    /// <summary>
    /// 时间分度常量（D-3 切分清退，2026-10-04）。
    ///
    /// ★来源说明：旧线把「一年月数」放在 <c>World.Biome.MonsoonSystem.MonthCount</c> 里，
    ///   于是 <c>LogicGrid</c>（CivSim 的运行网格）为了拿一个 **12** 而依赖整套季风生成器，
    ///   形成典型的「借常量形成的假依赖」——看起来是旧线消费者，实际只需要一个常量。
    ///   清退旧世界生成链时若不先把这类假依赖摘掉，就会误判「生成器还有消费者」而保留它。
    ///
    /// 本类不含任何生成能力，只是领域词汇，属保留项（C 类）。
    /// </summary>
    public static class Calendar
    {
        /// <summary>一年月数 = 12（存档月数据维度、作物季节循环、网格月度场统一口径）。</summary>
        public const int MonthsPerYear = 12;
    }
}
