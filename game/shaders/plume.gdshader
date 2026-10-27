shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never;

// Rocket exhaust plume: additive cone that fades along its length and at the silhouette, with flicker.
uniform float intensity = 1.0;
uniform float length_m = 4.0;
uniform vec3 core_color : source_color = vec3(1.0, 0.82, 0.55);
uniform vec3 edge_color : source_color = vec3(0.45, 0.55, 1.0);
varying float along;

void vertex() {
    along = clamp(-VERTEX.y / length_m, 0.0, 1.0);
}

void fragment() {
    float rim = abs(dot(normalize(NORMAL), normalize(VIEW)));
    float fl = 0.82 + 0.18 * sin(TIME * 55.0 + along * 18.0) * sin(TIME * 31.0 + along * 7.0);
    float a = pow(1.0 - along, 1.6) * pow(rim, 0.8) * intensity * fl;
    ALBEDO = mix(core_color, edge_color, smoothstep(0.0, 1.0, along)) * (1.0 + 1.5 * (1.0 - along));
    ALPHA = clamp(a, 0.0, 1.0);
}
