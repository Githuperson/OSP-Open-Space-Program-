shader_type spatial;
render_mode unshaded, cull_front, depth_draw_always;

// Navball: inner surface of a sphere seen through an orthographic camera. Texture coordinates come from the
// model-space direction so there is no UV seam: heading from atan(z, x) (north = -Z, east = +X), pitch from asin(y).
uniform sampler2D ball_tex : source_color, filter_linear, repeat_enable;
varying vec3 mp;

void vertex() { mp = normalize(VERTEX); }

void fragment() {
    float a = atan(mp.z, mp.x);
    float u = fract((a + 1.5707963) / 6.2831853);
    float v = 0.5 - asin(clamp(mp.y, -1.0, 1.0)) / 3.14159265;
    vec3 c = texture(ball_tex, vec2(u, v)).rgb;
    float facing = clamp(abs(dot(normalize(NORMAL), normalize(VIEW))), 0.0, 1.0);
    ALBEDO = c * (0.55 + 0.45 * pow(facing, 0.6));
}
