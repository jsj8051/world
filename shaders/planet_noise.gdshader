shader_type spatial;
render_mode unshaded, cull_disabled;

uniform float continent_freq  : hint_range(0.01, 0.5) = 0.12;
uniform float detail_freq     : hint_range(0.1, 2.0)  = 0.40;
uniform int   seed_offset     : hint_range(0, 9999)   = 42;
uniform float height_scale    : hint_range(0.0, 2.0)  = 0.40;
uniform float normal_eps      : hint_range(0.001, 1.0) = 0.05;

varying float v_elev;
varying vec3  v_pos;

// ═══════════════════  3D Simplex noise (MIT)  ═══════════════════

vec3 mod289(vec3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
vec4 mod289(vec4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
vec4 permute(vec4 x) { return mod289(((x * 34.0) + 10.0) * x); }
vec4 taylorInvSqrt(vec4 r) { return 1.79284291400159 - 0.85373472095314 * r; }

float snoise(vec3 v) {
    const vec2 C = vec2(1.0/6.0, 1.0/3.0);
    const vec4 D = vec4(0.0, 0.5, 1.0, 2.0);
    vec3 i = floor(v + dot(v, C.yyy));
    vec3 x0 = v - i + dot(i, C.xxx);
    vec3 g = step(x0.yzx, x0.xyz);
    vec3 l = 1.0 - g;
    vec3 i1 = min(g.xyz, l.zxy);
    vec3 i2 = max(g.xyz, l.zxy);
    vec3 x1 = x0 - i1 + C.xxx;
    vec3 x2 = x0 - i2 + C.yyy;
    vec3 x3 = x0 - D.yyy;
    i = mod289(i);
    vec4 p = permute(permute(permute(i.z + vec4(0.0, i1.z, i2.z, 1.0)) + i.y + vec4(0.0, i1.y, i2.y, 1.0)) + i.x + vec4(0.0, i1.x, i2.x, 1.0));
    float n_ = 0.142857142857; vec3 ns = n_ * D.wyz - D.xzx;
    vec4 j = p - 49.0 * floor(p * ns.z * ns.z);
    vec4 x_ = floor(j * ns.z); vec4 y_ = floor(j - 7.0 * x_);
    vec4 x = x_ * ns.x + ns.yyyy; vec4 y = y_ * ns.x + ns.yyyy;
    vec4 h = 1.0 - abs(x) - abs(y);
    vec4 b0 = vec4(x.xy, y.xy); vec4 b1 = vec4(x.zw, y.zw);
    vec4 s0 = floor(b0) * 2.0 + 1.0; vec4 s1 = floor(b1) * 2.0 + 1.0;
    vec4 sh = -step(h, vec4(0.0));
    vec4 a0 = b0.xzyw + s0.xzyw * sh.xxyy; vec4 a1 = b1.xzyw + s1.xzyw * sh.zzww;
    vec3 p0 = vec3(a0.xy, h.x); vec3 p1 = vec3(a0.zw, h.y);
    vec3 p2 = vec3(a1.xy, h.z); vec3 p3 = vec3(a1.zw, h.w);
    vec4 norm = taylorInvSqrt(vec4(dot(p0,p0),dot(p1,p1),dot(p2,p2),dot(p3,p3)));
    p0 *= norm.x; p1 *= norm.y; p2 *= norm.z; p3 *= norm.w;
    vec4 m = max(0.5 - vec4(dot(x0,x0),dot(x1,x1),dot(x2,x2),dot(x3,x3)), 0.0);
    m = m * m;
    return 105.0 * dot(m*m, vec4(dot(p0,x0),dot(p1,x1),dot(p2,x2),dot(p3,x3)));
}

float fbm(vec3 p, int oct, float lac, float gain) {
    float v=0.0,a=1.0,f=1.0,m=0.0;
    for (int i=0;i<8;i++){if(i>=oct)break;v+=a*snoise(p*f);m+=a;a*=gain;f*=lac;}
    return v/m;
}

float evaluate_raw(vec3 pos, float seed) {
    float wx = snoise(pos*continent_freq+vec3(seed, 0.0, 0.0));
    float wy = snoise(pos*continent_freq+vec3(seed+50.0, 0.0, 0.0));
    float wz = snoise(pos*continent_freq+vec3(seed+100.0, 0.0, 0.0));
    vec3 wp = pos + vec3(wx,wy,wz)*0.6;
    float c = snoise(wp*continent_freq+vec3(seed, 0.0, 0.0));
    float mr = fbm(pos*detail_freq*0.9+vec3(seed+200.0, 0.0, 0.0), 5, 2.1, 0.45);
    float mt = -abs(mr);
    float h = fbm(pos*detail_freq*1.5+vec3(seed+300.0, 0.0, 0.0), 4, 2.0, 0.5);
    float e;
    if (c > -0.05) { e = (c+0.05)*0.55 + mt*0.35*clamp(c+0.5, 0.0, 1.0) + h*0.15; }
    else { e = (c+0.05)*0.7 + h*0.05; }
    float lat = abs(asin(clamp(normalize(pos).y, -1.0, 1.0)));
    e -= clamp((lat-0.3)/0.9, 0.0, 1.0)*0.15;
    return e;
}

float evaluate(vec3 pos, float seed) {
    return (evaluate_raw(pos, seed) + 0.85) / 1.2 * 2.0 - 1.0;
}

vec3 colorize(float e, vec3 pos) {
    float lat = abs(asin(clamp(normalize(pos).y, -1.0, 1.0)));
    if (lat > 1.2) return mix(vec3(0.85,0.88,0.95), vec3(0.95,0.97,1.0), clamp((lat-1.2)/0.37, 0.0, 1.0));
    const vec3 dO=vec3(0.02,0.10,0.25), sO=vec3(0.06,0.35,0.60), be=vec3(0.70,0.65,0.40);
    const vec3 lo=vec3(0.30,0.65,0.10), hi=vec3(0.50,0.50,0.08), mt=vec3(0.60,0.45,0.10), sn=vec3(0.95,0.97,1.0);
    if (e < 0.15) return mix(dO, sO, 1.0 - clamp((0.15-e)/1.15, 0.0, 1.0));
    if (e < 0.28) return mix(sO, be, (e-0.15)/0.13);
    if (e < 0.33) return be;
    if (e < 0.50) return mix(be, lo, (e-0.33)/0.17);
    if (e < 0.65) return mix(lo, hi, (e-0.50)/0.15);
    if (e < 0.80) return mix(hi, mt, (e-0.65)/0.15);
    return mix(mt, sn, clamp((e-0.80)/0.20, 0.0, 1.0));
}

// ═══════════════════  Vertex  ═══════════════════

void vertex() {
    float seed = float(seed_offset);
    vec3 up = normalize(VERTEX);
    vec3 pos = VERTEX;

    float n = evaluate_raw(pos, seed);

    // Gradient normals
    vec3 ref = abs(up.y) < 0.999 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 t1 = normalize(cross(up, ref));
    vec3 t2 = cross(up, t1);

    float h1 = evaluate_raw(pos + t1 * normal_eps, seed);
    float h2 = evaluate_raw(pos + t2 * normal_eps, seed);

    float dn1 = (h1 - n) / normal_eps;
    float dn2 = (h2 - n) / normal_eps;

    vec3 surface_normal = normalize(up - (t1 * dn1 + t2 * dn2) * height_scale);

    float elev_norm = (n + 0.85) / 1.2 * 2.0 - 1.0;
    v_elev = elev_norm;
    v_pos = pos;
    VERTEX += surface_normal * elev_norm * height_scale;

    NORMAL = surface_normal;
}

// ═══════════════════  Fragment  ═══════════════════

void fragment() {
    float e = evaluate(v_pos, float(seed_offset));
    ALBEDO = colorize(e, v_pos);
}
