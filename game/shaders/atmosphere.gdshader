shader_type spatial;
render_mode unshaded, blend_add, cull_front, depth_test_disabled, depth_draw_never;

// Single-scattering atmosphere (Rayleigh + Mie) integrated analytically along each view ray and limited by scene depth.
// All lengths are in the (possibly scaled) render units; inv_scale converts them back to metres for optical depth.

uniform sampler2D depth_tex : hint_depth_texture, repeat_disable, filter_nearest;
uniform vec3 center_world = vec3(0.0);
uniform float planet_r = 600000.0;
uniform float atmo_r = 670000.0;
uniform float inv_scale = 1.0;
uniform float scale_h = 5600.0;
uniform vec3 sun_dir = vec3(1.0, 0.0, 0.0);
uniform vec3 beta_r = vec3(5.8e-6, 13.5e-6, 33.1e-6);
uniform float beta_m = 21e-6;
uniform float intensity = 7.0;
uniform float mie_g = 0.76;
uniform float night_floor = 0.0;
uniform int steps = 12;

vec2 ray_sphere(vec3 ro, vec3 rd, float r) {
    float b = dot(ro, rd);
    float c = dot(ro, ro) - r * r;
    float h = b * b - c;
    if (h < 0.0) return vec2(-1.0, -1.0);
    h = sqrt(h);
    return vec2(-b - h, -b + h);
}

void fragment() {
    vec3 ro_w = (INV_VIEW_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz;
    vec3 rd_v = normalize(VERTEX);
    vec3 rd = normalize((INV_VIEW_MATRIX * vec4(rd_v, 0.0)).xyz);
    vec3 o = ro_w - center_world;

    vec2 ta = ray_sphere(o, rd, atmo_r);
    if (ta.y < 0.0) discard;
    float t0 = max(ta.x, 0.0), t1 = ta.y;
    vec2 tp = ray_sphere(o, rd, planet_r);
    if (tp.x > 0.0) t1 = min(t1, tp.x);

    float d = texture(depth_tex, SCREEN_UV).r;
    float scene_dist = 1.0e20;
    if (d > 0.0) {
        vec4 ndc = vec4(SCREEN_UV * 2.0 - 1.0, d, 1.0);
        vec4 v = INV_PROJECTION_MATRIX * ndc;
        v.xyz /= v.w;
        scene_dist = length(v.xyz);
    }
    t1 = min(t1, scene_dist);
    if (t1 <= t0) discard;

    float dt = (t1 - t0) / float(steps);
    float hm = scale_h * 0.2;
    float optR = 0.0, optM = 0.0;
    vec3 sumR = vec3(0.0), sumM = vec3(0.0);
    float mu = dot(rd, sun_dir);
    float phaseR = 3.0 / (16.0 * 3.14159265) * (1.0 + mu * mu);
    float g2 = mie_g * mie_g;
    float phaseM = 3.0 / (8.0 * 3.14159265) * ((1.0 - g2) * (1.0 + mu * mu)) / ((2.0 + g2) * pow(1.0 + g2 - 2.0 * mie_g * mu, 1.5));

    for (int i = 0; i < steps; i++) {
        vec3 p = o + rd * (t0 + (float(i) + 0.5) * dt);
        float h = (length(p) - planet_r) * inv_scale;
        float dR = exp(-h / scale_h) * dt * inv_scale;
        float dM = exp(-h / hm) * dt * inv_scale;
        optR += dR; optM += dM;

        // light ray to the top of the atmosphere
        vec2 tl = ray_sphere(p, sun_dir, atmo_r);
        vec2 tpl = ray_sphere(p, sun_dir, planet_r);
        if (tpl.x > 0.0) continue; // in planet shadow
        float lr = 0.0, lm = 0.0;
        float ldt = tl.y / 4.0;
        for (int k = 0; k < 4; k++) {
            vec3 q = p + sun_dir * ((float(k) + 0.5) * ldt);
            float hq = (length(q) - planet_r) * inv_scale;
            lr += exp(-hq / scale_h) * ldt * inv_scale;
            lm += exp(-hq / hm) * ldt * inv_scale;
        }
        vec3 tau = beta_r * (optR + lr) + vec3(beta_m * 1.1) * (optM + lm);
        vec3 att = exp(-tau);
        sumR += att * dR;
        sumM += att * dM;
    }
    vec3 col = intensity * (sumR * beta_r * phaseR + sumM * vec3(beta_m) * phaseM);
    col = vec3(1.0) - exp(-col * 2.2);
    ALBEDO = col;
}
