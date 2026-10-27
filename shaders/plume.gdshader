// Additive rocket exhaust plume (see src/Game/Rendering/EngineFx.cs).
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

uniform vec4 core_color : source_color = vec4(1.0, 0.8, 0.5, 1.0);
uniform vec4 edge_color : source_color = vec4(1.0, 0.45, 0.15, 1.0);
uniform float intensity = 1.0;
uniform float time_offset = 0.0;
varying float v_along;

void vertex() {
	v_along = clamp(-VERTEX.y, 0.0, 1.0);
}

void fragment() {
	float t = TIME * 23.0 + time_offset;
	float flicker = 0.85 + 0.15 * sin(t + v_along * 40.0) * sin(t * 1.7 + v_along * 13.0);
	float facing = abs(dot(normalize(NORMAL), normalize(VIEW)));
	float core = pow(facing, 2.5);
	float fade = pow(1.0 - v_along, 1.6);
	float diamonds = 0.75 + 0.25 * sin(v_along * 38.0 - TIME * 4.0);
	vec3 col = mix(edge_color.rgb, core_color.rgb, core) * diamonds;
	float a = fade * (0.25 + 0.75 * core) * flicker * intensity;
	ALBEDO = col * a * 2.0;
	ALPHA = clamp(a, 0.0, 1.0);
}
