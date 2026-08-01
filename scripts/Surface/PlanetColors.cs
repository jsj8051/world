using Godot;

namespace World.Surface
{

    /// <summary>
    /// Shared planet coloring utilities.
    /// </summary>
    public static class PlanetColors
    {
        /// <summary>
        /// Elevation color ramp (normalized [-1, 1], 0 = 海平面):
        /// deep ocean → ocean → shore → lowland → highland → snow.
        /// 海洋 (-1..0) 全为蓝色系，沙滩色只在潮间带 (0..0.05)，
        /// 避免浅海过渡段混出灰青色"伪陆地"，与 biome 图视觉一致。
        /// </summary>
        public static Color ElevationToColor(float e)
        {
            if (e < -0.05f)
            {
                float t = Mathf.Clamp((-e - 0.05f) / 0.95f, 0f, 1f); // 深海 → 浅海渐变
                return new Color(0.01f, 0.05f, 0.18f).Lerp(new Color(0.05f, 0.30f, 0.55f), 1f - t);
            }
            if (e < 0.0f)
            {
                float t = (e + 0.05f) / 0.05f; // 浅海微亮
                return new Color(0.05f, 0.30f, 0.55f).Lerp(new Color(0.12f, 0.45f, 0.68f), t);
            }
            if (e < 0.05f)
            {
                float t = e / 0.05f; // 潮间带：浅蓝 → 沙滩
                return new Color(0.12f, 0.45f, 0.68f).Lerp(new Color(0.70f, 0.65f, 0.40f), t);
            }
            if (e < 0.35f)
            {
                float t = (e - 0.05f) / 0.30f; // 低地：沙滩 → 绿
                return new Color(0.70f, 0.65f, 0.40f).Lerp(new Color(0.30f, 0.65f, 0.10f), t);
            }
            if (e < 0.65f)
            {
                float t = (e - 0.35f) / 0.30f; // 高地：绿 → 黄
                return new Color(0.30f, 0.65f, 0.10f).Lerp(new Color(0.50f, 0.50f, 0.08f), t);
            }
            float s = Mathf.Clamp((e - 0.65f) / 0.35f, 0f, 1f); // 雪顶：黄 → 白
            return new Color(0.50f, 0.50f, 0.08f).Lerp(new Color(0.95f, 0.97f, 1.00f), s);
        }
    }
}
