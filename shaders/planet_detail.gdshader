shader_type spatial;
render_mode cull_disabled;

// Flat per-tile vertex color (COLOR) + procedural 3D value-noise detail.
// Noise coordinates = normalized model-space position (≈ unit sphere direction),
// so detail is glued to the surface, not the camera. Two octaves: broad
// (~1/40 rad ≈ 160km at r=6330) and fine (~1/120 rad ≈ 53km) break up the
// flat-color plastic look with zero texture assets.

varying vec3 noise_coord;

void vertex()
{
	noise_coord = normalize(VERTEX);
}

float hash(vec3 p)
{
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float noise(vec3 p)
{
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = mix(hash(i), hash(i + vec3(1.0, 0.0, 0.0)), f.x);
	float b = mix(hash(i + vec3(0.0, 1.0, 0.0)), hash(i + vec3(1.0, 1.0, 0.0)), f.x);
	float c = mix(hash(i + vec3(0.0, 0.0, 1.0)), hash(i + vec3(1.0, 0.0, 1.0)), f.x);
	float d = mix(hash(i + vec3(0.0, 1.0, 1.0)), hash(i + vec3(1.0, 1.0, 1.0)), f.x);
	return mix(mix(a, b, f.y), mix(c, d, f.y), f.z);
}

void fragment()
{
	float n = noise(noise_coord * 40.0) * 0.5 + noise(noise_coord * 120.0) * 0.25;
	ALBEDO = COLOR.rgb * (0.85 + 0.30 * n);
	ROUGHNESS = 0.85;
}
