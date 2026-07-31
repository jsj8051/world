shader_type spatial;
render_mode cull_disabled;

// Flat per-tile vertex color only — clean cell look.
void fragment()
{
	ALBEDO = COLOR.rgb;
	ROUGHNESS = 0.85;
}
