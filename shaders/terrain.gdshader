// Planet terrain: vertex colours from the procedural generator, alpha = water mask.
// Adds world-anchored detail noise so close-up ground is not flat.
shader_type spatial;
render_mode cull_back, diffuse_burley, specular_schlick_ggx;

instance uniform vec3 detail_origin;  // chunk origin in body-fixed metres (stable detail noise)
uniform float detail_strength = 0.12;

varying vec3 v_bodypos;

float hash(vec3 p) { return fract(sin(dot(p, vec3(12.9898, 78.233, 37.719))) * 43758.5453); }
float vnoise(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix(hash(i), hash(i + vec3(1,0,0)), f.x), mix(hash(i + vec3(0,1,0)), hash(i + vec3(1,1,0)), f.x), f.y),
	           mix(mix(hash(i + vec3(0,0,1)), hash(i + vec3(1,0,1)), f.x), mix(hash(i + vec3(0,1,1)), hash(i + vec3(1,1,1)), f.x), f.y), f.z);
}

void vertex() {
	v_bodypos = VERTEX + detail_origin;
}

void fragment() {
	vec3 p = mod(v_bodypos, 10000.0);
	float n = vnoise(p * 0.35) * 0.5 + vnoise(p * 1.7) * 0.3 + vnoise(p * 7.0) * 0.2;
	float water = COLOR.a;
	vec3 base = COLOR.rgb * (1.0 - detail_strength * (1.0 - water) + detail_strength * 2.0 * n * (1.0 - water));
	ALBEDO = base;
	ROUGHNESS = mix(0.92, 0.12, water);
	SPECULAR = mix(0.25, 0.7, water);
	METALLIC = 0.0;
}
