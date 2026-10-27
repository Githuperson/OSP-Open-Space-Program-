shader_type spatial;
render_mode cull_back;

// Terrain/ocean shading with procedural micro-detail. Vertex COLOR.rgb = biome albedo, COLOR.a = 1 land / 0 ocean.
// UV.xy + UV2.x carry the planet-fixed position in kilometres so detail noise stays attached to the ground.

varying vec3 pos_m;
varying float view_dist;
uniform float detail_strength = 1.0;
uniform float ocean_gloss = 1.0;

uint hash_u(uvec3 v) {
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z; v.y += v.z * v.x; v.z += v.x * v.y;
    return v.x ^ v.y ^ v.z;
}

float hash3(vec3 p) {
    ivec3 i = ivec3(floor(p));
    return float(hash_u(uvec3(i + ivec3(100000)))) / 4294967295.0;
}

float vnoise(vec3 p) {
    vec3 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = hash3(i + vec3(0,0,0)), n100 = hash3(i + vec3(1,0,0));
    float n010 = hash3(i + vec3(0,1,0)), n110 = hash3(i + vec3(1,1,0));
    float n001 = hash3(i + vec3(0,0,1)), n101 = hash3(i + vec3(1,0,1));
    float n011 = hash3(i + vec3(0,1,1)), n111 = hash3(i + vec3(1,1,1));
    return mix(mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
               mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y), f.z);
}

void vertex() {
    pos_m = vec3(UV, UV2.x) * 1000.0;
    view_dist = length((MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz);
}

void fragment() {
    float land = COLOR.a;
    vec3 col = COLOR.rgb;
    float fade = 1.0 - smoothstep(300.0, 6000.0, view_dist);
    float n = 0.0;
    if (fade > 0.001 && land > 0.5) {
        n = vnoise(pos_m / 2.3) * 0.45 + vnoise(pos_m / 9.0) * 0.3 + vnoise(pos_m / 41.0) * 0.25;
        n = (n - 0.5) * 2.0;
    }
    float mid = (vnoise(pos_m / 400.0) - 0.5) * 0.18 * land;
    col *= 1.0 + mid + n * 0.28 * fade * detail_strength;
    ALBEDO = col;
    ROUGHNESS = mix(0.07, 0.92, land);
    METALLIC = 0.0;
    SPECULAR = mix(0.9 * ocean_gloss, 0.25, land);
}
