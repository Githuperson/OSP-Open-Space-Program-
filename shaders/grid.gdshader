// Editor hangar floor with a fading measurement grid.
shader_type spatial;
render_mode cull_disabled;
uniform vec4 base_color : source_color = vec4(0.16, 0.17, 0.19, 1.0);
uniform vec4 line_color : source_color = vec4(0.32, 0.45, 0.6, 1.0);
varying vec3 world_pos;
void vertex() { world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }
void fragment() {
	vec2 p = world_pos.xz;
	vec2 g1 = abs(fract(p - 0.5) - 0.5) / fwidth(p);
	vec2 g5 = abs(fract(p / 5.0 - 0.5) - 0.5) / fwidth(p / 5.0);
	float l1 = 1.0 - min(min(g1.x, g1.y), 1.0);
	float l5 = 1.0 - min(min(g5.x, g5.y), 1.0);
	float fade = exp(-length(p) * 0.03);
	vec3 col = mix(base_color.rgb, line_color.rgb, max(l1 * 0.35, l5) * fade);
	ALBEDO = col;
	ROUGHNESS = 0.7;
	METALLIC = 0.1;
}
