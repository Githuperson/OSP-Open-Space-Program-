// Small shared helpers.

export const clamp = (x, a, b) => (x < a ? a : x > b ? b : x);
export const lerp = (a, b, t) => a + (b - a) * t;
export const smoothstep = (a, b, x) => {
  const t = clamp((x - a) / (b - a), 0, 1);
  return t * t * (3 - 2 * t);
};
export const DEG = Math.PI / 180;
export const TAU = Math.PI * 2;
export const mod = (x, m) => ((x % m) + m) % m;

export function fmtTime(sec) {
  sec = Math.max(0, Math.floor(sec));
  const d = Math.floor(sec / 86400);
  const h = Math.floor((sec % 86400) / 3600);
  const m = Math.floor((sec % 3600) / 60);
  const s = sec % 60;
  const p = (n) => String(n).padStart(2, '0');
  return `${d}d ${p(h)}:${p(m)}:${p(s)}`;
}

export function fmtTimeShort(sec) {
  sec = Math.max(0, sec);
  const d = Math.floor(sec / 86400);
  const h = Math.floor((sec % 86400) / 3600);
  const m = Math.floor((sec % 3600) / 60);
  const s = Math.floor(sec % 60);
  const p = (n) => String(n).padStart(2, '0');
  return d > 0 ? `${d}d ${p(h)}:${p(m)}:${p(s)}` : `${p(h)}:${p(m)}:${p(s)}`;
}

export function fmtDist(m) {
  const a = Math.abs(m);
  if (a < 1000) return `${m.toFixed(1)} m`;
  if (a < 1e6) return `${(m / 1e3).toFixed(2)} km`;
  if (a < 1e9) return `${(m / 1e6).toFixed(2)} Mm`;
  return `${(m / 1e9).toFixed(3)} Gm`;
}

export function fmtVel(v) {
  const a = Math.abs(v);
  if (a < 1000) return `${v.toFixed(1)} m/s`;
  return `${(v / 1e3).toFixed(2)} km/s`;
}

export function fmtMass(kg) {
  if (Math.abs(kg) < 1000) return `${kg.toFixed(0)} kg`;
  return `${(kg / 1e3).toFixed(2)} t`;
}

export function fmtAngle(deg) {
  return `${deg.toFixed(1)}°`;
}

// Classic "viridis-ish" colormap, t in [0,1].
export function heatColor(t) {
  t = clamp(t, 0, 1);
  const stops = [
    [0.00, 0.267, 0.005, 0.329],
    [0.25, 0.190, 0.529, 0.745],
    [0.50, 0.127, 0.745, 0.663],
    [0.75, 0.566, 0.851, 0.345],
    [1.00, 0.993, 0.906, 0.144],
  ];
  for (let i = 1; i < stops.length; i++) {
    if (t <= stops[i][0]) {
      const a = stops[i - 1], b = stops[i];
      const f = (t - a[0]) / (b[0] - a[0]);
      return [
        lerp(a[1], b[1], f),
        lerp(a[2], b[2], f),
        lerp(a[3], b[3], f),
      ];
    }
  }
  return [1, 1, 0];
}

export function heatColorCss(t) {
  const [r, g, b] = heatColor(t);
  return `rgb(${Math.round(r * 255)},${Math.round(g * 255)},${Math.round(b * 255)})`;
}
