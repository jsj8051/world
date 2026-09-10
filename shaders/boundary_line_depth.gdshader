shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_always, cull_disabled;

// 板块边界线两遍制·第一遍(深度预写,2026-09-10):线几何(线带 + 角点端帽,见
// BallMesh.BuildBoundaryLineMesh)以 ALPHA=0 隐形光栅化,但【写深度】——深度测试
// (对格面)通过的片元按"更近者覆盖"沉淀出该像素上线几何的最近深度;被星球挡住的
// 背面片元不写。线带与端帽在角点处必然几何重叠、弦高各不相同,单遍"首片元入深度"
// 挡不住弦高恰更近的后到片元(实测端帽仍双混变深);先全体预写深度、第二遍
// (boundary_line_draw)只画深度命中的最近层 → 每像素恰好混合一次,线色 alpha 任意
// 也不叠加变深。遍顺序由 BallView 的 render_priority 钉住(透明队列按深度排序会乱)。
void fragment()
{
	ALBEDO = vec3(0.0);
	ALPHA = 0.0;
}
