shader_type sky;

uniform float star_density = 0.992;
uniform float brightness = 1.0;

float h31(vec3 p) { p = fract(p * 0.3183099 + vec3(0.1, 0.2, 0.3)); p *= 17.0; return fract(p.x * p.y * p.z * (p.x + p.y + p.z)); }
vec3 h33(vec3 p) { return vec3(h31(p), h31(p + 17.3), h31(p + 41.7)); }

float vn(vec3 p) {
    vec3 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(h31(i), h31(i + vec3(1,0,0)), f.x), mix(h31(i + vec3(0,1,0)), h31(i + vec3(1,1,0)), f.x), f.y),
               mix(mix(h31(i + vec3(0,0,1)), h31(i + vec3(1,0,1)), f.x), mix(h31(i + vec3(0,1,1)), h31(i + vec3(1,1,1)), f.x), f.y), f.z);
}

void sky() {
    vec3 d = normalize(EYEDIR);
    vec3 col = vec3(0.0);
    for (int layer = 0; layer < 3; layer++) {
        float scale = 60.0 + float(layer) * 70.0;
        vec3 p = d * scale;
        vec3 ip = floor(p);
        vec3 f = fract(p) - 0.5;
        float r = h31(ip + float(layer) * 11.0);
        if (r > star_density) {
            vec3 off = (h33(ip + 3.0) - 0.5) * 0.6;
            float dist = length(f - off);
            float s = smoothstep(0.12, 0.0, dist);
            float mag = (r - star_density) / (1.0 - star_density);
            vec3 tint = mix(vec3(0.7, 0.8, 1.0), vec3(1.0, 0.85, 0.7), h31(ip + 9.0));
            col += tint * s * (0.35 + 1.4 * mag * mag) * brightness;
        }
    }
    // faint galactic band
    float band = exp(-pow(dot(d, normalize(vec3(0.2, 1.0, 0.35))) * 3.2, 2.0));
    float cloud = vn(d * 5.0) * 0.6 + vn(d * 13.0) * 0.4;
    col += vec3(0.05, 0.06, 0.1) * band * cloud * brightness;
    COLOR = col;
}
