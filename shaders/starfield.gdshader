// Procedural starfield with a faint galactic band and the star's disc.
// Used as the sky of the scaled-space world (see src/Game/Rendering/ScaledSpace.cs).
shader_type sky;

uniform float star_density = 1.0;     // multiplier on the per-cell star probability
uniform float star_brightness = 1.6;
uniform float band_strength = 0.12;
uniform vec3 band_normal = vec3(0.21, 0.94, 0.27);
uniform float sun_size = 0.0175;
uniform vec4 sun_color : source_color = vec4(1.0, 0.95, 0.85, 1.0);
uniform float sky_brightness = 0.0;    // set by the game: >0 inside an atmosphere hides stars
uniform vec4 day_color : source_color = vec4(0.3, 0.52, 0.95, 1.0);

vec3 hash3(vec3 p) {
	p = vec3(dot(p, vec3(127.1, 311.7, 74.7)), dot(p, vec3(269.5, 183.3, 246.1)), dot(p, vec3(113.5, 271.9, 124.6)));
	return fract(sin(p) * 43758.5453123);
}

float noise3(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float n = 0.0;
	n = mix(mix(mix(hash3(i).x, hash3(i + vec3(1,0,0)).x, f.x), mix(hash3(i + vec3(0,1,0)).x, hash3(i + vec3(1,1,0)).x, f.x), f.y),
	        mix(mix(hash3(i + vec3(0,0,1)).x, hash3(i + vec3(1,0,1)).x, f.x), mix(hash3(i + vec3(0,1,1)).x, hash3(i + vec3(1,1,1)).x, f.x), f.y), f.z);
	return n;
}

float stars(vec3 dir, float scale, float probability) {
	vec3 p = dir * scale;
	vec3 cell = floor(p);
	float result = 0.0;
	for (int x = -1; x <= 1; x++)
	for (int y = -1; y <= 1; y++)
	for (int z = -1; z <= 1; z++) {
		vec3 c = cell + vec3(float(x), float(y), float(z));
		vec3 h = hash3(c);
		if (h.z > probability * star_density) continue;
		vec3 starPos = normalize(c + h);
		float d = acos(clamp(dot(starPos, dir), -1.0, 1.0)) * scale;
		float size = 0.06 + 0.12 * h.y;
		result += smoothstep(size, 0.0, d) * (0.3 + 0.7 * h.x * h.x);
	}
	return result;
}

void sky() {
	vec3 dir = normalize(EYEDIR);
	// ~5k brighter stars plus ~6k faint ones over the whole sky.
	float s = stars(dir, 160.0, 0.016) + 0.55 * stars(dir, 360.0, 0.0035);
	float band = exp(-pow(dot(dir, normalize(band_normal)) * 4.0, 2.0));
	float cloud = noise3(dir * 6.0) * 0.6 + noise3(dir * 15.0) * 0.4;
	vec3 milky = vec3(0.55, 0.6, 0.85) * band * cloud * band_strength;
	vec3 nebula = vec3(0.5, 0.18, 0.35) * pow(noise3(dir * 3.0 + 7.0), 4.0) * 0.18;
	vec3 col = vec3(s) * star_brightness + milky + nebula;
	// Stars fade out in daylight inside an atmosphere.
	col *= clamp(1.0 - sky_brightness * 1.5, 0.0, 1.0);
	if (LIGHT0_ENABLED) {
		float d = acos(clamp(dot(dir, LIGHT0_DIRECTION), -1.0, 1.0));
		float disc = smoothstep(sun_size, sun_size * 0.85, d);
		float glow = exp(-d * 18.0) * 0.6 + exp(-d * 4.0) * 0.08;
		col += sun_color.rgb * (disc * 30.0 + glow * 2.0);
	}
	COLOR = col;
}
