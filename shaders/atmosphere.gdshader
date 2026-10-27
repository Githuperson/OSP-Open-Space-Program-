// Single-scattering approximation for a planetary atmosphere, rendered on a shell in scaled space.
// The view ray is intersected analytically with the planet and atmosphere spheres, then sampled.
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, depth_test_disabled, cull_front, shadows_disabled, fog_disabled;

uniform vec3 planet_center;      // world position (scaled units)
uniform float planet_radius;
uniform float atmo_radius;
uniform float scale_height;
uniform vec3 sun_dir;            // towards the star
uniform vec4 sky_color : source_color = vec4(0.3, 0.52, 0.95, 1.0);
uniform vec4 sunset_color : source_color = vec4(1.0, 0.45, 0.2, 1.0);
uniform float density = 1.0;     // optical thickness multiplier
uniform float intensity = 1.0;

vec2 ray_sphere(vec3 ro, vec3 rd, vec3 c, float r) {
	vec3 oc = ro - c;
	float b = dot(oc, rd);
	float cc = dot(oc, oc) - r * r;
	float h = b * b - cc;
	if (h < 0.0) return vec2(1e30, -1e30);
	h = sqrt(h);
	return vec2(-b - h, -b + h);
}

void fragment() {
	vec3 world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	vec3 ro = CAMERA_POSITION_WORLD;
	vec3 rd = normalize(world - ro);
	vec2 a = ray_sphere(ro, rd, planet_center, atmo_radius);
	if (a.x > a.y) discard;
	float t0 = max(a.x, 0.0);
	float t1 = a.y;
	vec2 p = ray_sphere(ro, rd, planet_center, planet_radius);
	if (p.x < p.y && p.x > 0.0) t1 = min(t1, p.x);
	if (t1 <= t0) discard;
	const int STEPS = 12;
	float dt = (t1 - t0) / float(STEPS);
	// Optical depth per unit density-length: a vertical column has tau ≈ 0.3 * density.
	float beta = density * 0.3 / scale_height;
	float optical = 0.0;
	vec3 scatter = vec3(0.0);
	for (int i = 0; i < STEPS; i++) {
		vec3 pos = ro + rd * (t0 + dt * (float(i) + 0.5));
		vec3 up = pos - planet_center;
		float h = max(length(up) - planet_radius, 0.0);
		float dtau = beta * exp(-h / scale_height) * dt;
		float sunUp = dot(normalize(up), sun_dir);
		float lit = smoothstep(-0.18, 0.12, sunUp);
		float red = smoothstep(0.35, 0.0, sunUp) * lit;
		vec3 c = mix(sky_color.rgb, sunset_color.rgb, red * 0.85);
		scatter += c * lit * dtau * exp(-optical);
		optical += dtau;
	}
	float mu = dot(rd, sun_dir);
	float phase = 0.75 * (1.0 + mu * mu) + 0.5 * pow(max(mu, 0.0), 8.0);
	vec3 col = scatter * phase * intensity;
	ALBEDO = col;
	ALPHA = 1.0;
}
