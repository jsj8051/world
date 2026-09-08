shader_type spatial;
render_mode unshaded;

// 格色面材质 + 板块边界描边（2026-09-08 用户拍板：弃"独立线几何 + 深度微偏"路线，改老树
// planet_detail 同款【描边轮廓】——边界权重烘进顶点色 alpha，片元按插值权重压暗格色成轮廓带）。
// 描边与格面同一表面、同半径 → 零浮起、零深度竞争（它本来就是面的一部分），球剪影处不悬空、
// 不闪断；带宽随格尺寸自适应（= outline_width × 格心到边距离），分辨率/半径变化无需调参。
// COLOR.rgb = 格色（C# 侧已 sRGB→linear）；COLOR.a = 边界权重（1 = 格心/内部角点，0 = 贴边界角点）。
uniform float outline_darken : hint_range(0.0, 1.0) = 0.0;    // 描边带压暗系数（用户已调 0 = 纯黑线）
uniform float outline_width : hint_range(0.0, 0.5) = 0.18;    // 带半宽（× 格心到边距离；两侧格各出一条 → 总宽 ≈ 2×0.18 格心距）

void fragment()
{
	// 锐边方案（2026-09-09 用户反馈旧双停点宽渐变窗 smoothstep(0.12,0.22) 显糊——带的近半是
	// 过渡区、无实心核心，两侧格各一层渐变叠加更糊）：改单阈值切实心带 + fwidth 屏幕空间
	// 导数做恰好 1px 的抗锯齿边——任何缩放/掠射角下锐利且不闪（MSAA 只平滑几何边，帮不上渐变）。
	float aa = fwidth(COLOR.a);
	float edge = smoothstep(outline_width - aa, outline_width + aa, COLOR.a);
	ALBEDO = mix(COLOR.rgb * outline_darken, COLOR.rgb, edge);
}
