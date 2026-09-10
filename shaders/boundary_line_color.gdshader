shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled;

// 板块边界线两遍制·第二遍(上色,2026-09-10),作 boundary_line_depth 的 NextPass:
// 在第一遍沉淀的线几何最近深度上做常规深度测试(反向 Z 下"更近或等深"通过)——
// 同像素上只有深度恰为最近层的那组片元通过,更远的重叠层全部落选 → 每像素恰好
// 混合一次线色:半透明线色也绝不叠加变深,端帽/三板交汇处处与线身同色度。
// (stencil 两遍制已试已弃:模板只能挡跨物体重复,挡不住同网格自重叠。)
uniform vec4 line_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);   // a=1 退化为不透明线

void fragment()
{
	ALBEDO = line_color.rgb;
	ALPHA = line_color.a;
}
