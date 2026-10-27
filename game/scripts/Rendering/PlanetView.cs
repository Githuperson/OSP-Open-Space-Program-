using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using OSP.Core.Bodies;
using OSP.Core.Mathx;

namespace OSP.Game.Rendering;

/// <summary>
/// Renders one celestial body: a cube-sphere quadtree of procedurally generated terrain chunks (level of detail chosen
/// from the camera distance, horizon culled, built on worker threads) plus an atmosphere shell. Positions are supplied
/// camera-relative in double precision (floating origin), so nothing is ever placed at planetary distances in float.
/// </summary>
public sealed partial class PlanetView : Node3D
{
    public CelestialBody Body { get; }
    public double RenderScale { get; private set; } = 1.0;
    public int ActiveChunks => _drawn.Count;
    public int CachedChunks => _chunks.Count;
    public double SplitFactor { get; set; } = 2.2;
    /// <summary>Half-angle (rad) of the view cone used to skip chunks that cannot be seen (set by the owning scene).</summary>
    public double ViewConeHalfAngle { get; set; } = 1.15;
    public Vec3d CameraForwardWorld { get; set; } = new(0, 0, -1);

    /// <summary>Distance up to which bodies are drawn at true scale; beyond it distances are compressed logarithmically
    /// (angular size is preserved because the whole body is scaled about the camera).</summary>
    public const double TrueScaleDistance = 2.0e5;
    public static double CompressionScale(double d) => d <= TrueScaleDistance ? 1.0 : TrueScaleDistance * (1.0 + Math.Log(d / TrueScaleDistance)) / d;
    public int MaxLevelCap { get; set; } = 99;

    private sealed class Chunk
    {
        public ChunkKey Key;
        public MeshInstance3D? Node;
        public Vec3d Center;
        public bool Building;
        public long LastUsed;
    }

    private readonly Dictionary<ChunkKey, Chunk> _chunks = new();
    private readonly ConcurrentQueue<ChunkData> _finished = new();
    private readonly HashSet<ChunkKey> _drawn = new();
    private readonly int _maxLevel;
    private int _inFlight;
    private int _lastRequests;
    private long _frame;
    private ShaderMaterial _terrainMat = null!;
    private MeshInstance3D? _atmoNode;
    private ShaderMaterial? _atmoMat;
    private MeshInstance3D? _simpleSphere;
    private static Shader? _terrainShader, _atmoShader;

    public int MaxBuildsPerFrame { get; set; } = 3;
    public int MaxConcurrentBuilds { get; set; } = 3;

    public PlanetView(CelestialBody body)
    {
        Body = body; Name = "Planet_" + body.Id;
        double size0 = PlanetChunkBuilder.ChunkSize(body.Radius, 0);
        _maxLevel = Math.Max(3, (int)Math.Ceiling(Math.Log2(size0 / 45.0)));

        if (body.Terrain != null)
        {
            _terrainShader ??= GD.Load<Shader>("res://shaders/planet.gdshader");
            _terrainMat = new ShaderMaterial { Shader = _terrainShader };
        }
        else BuildSimpleSphere();

        if (body.Atmosphere != null) BuildAtmosphere();
    }

    // ------------------------------------------------------------------------------------------------------------
    private void BuildSimpleSphere()
    {
        // Stars (and any body without terrain) are plain emissive spheres.
        var mesh = MeshFactory.Sphere(1.0, 64);
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color((float)Body.Color[0], (float)Body.Color[1], (float)Body.Color[2]),
        };
        _simpleSphere = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_simpleSphere);
    }

    private void BuildAtmosphere()
    {
        var atm = Body.Atmosphere!;
        _atmoShader ??= GD.Load<Shader>("res://shaders/atmosphere.gdshader");
        _atmoMat = new ShaderMaterial { Shader = _atmoShader };
        var mesh = MeshFactory.Sphere(1.0, 64, 32);
        _atmoNode = new MeshInstance3D { Mesh = mesh, MaterialOverride = _atmoMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, CustomAabb = new Aabb(new Vector3(-1.1f, -1.1f, -1.1f), new Vector3(2.2f, 2.2f, 2.2f)) };
        AddChild(_atmoNode);
        // Rayleigh coefficients scaled so the vertical optical depth is planet-independent (reference scale height 8 km).
        double k = 8000.0 / atm.ScaleHeight;
        var sc = atm.ScatterColor;
        // Tint the standard Rayleigh spectrum toward the body's scatter colour.
        _atmoMat.SetShaderParameter("beta_r", new Vector3((float)(5.8e-6 * k * (0.35 + 1.3 * sc[0])), (float)(13.5e-6 * k * (0.35 + 1.3 * sc[1])), (float)(33.1e-6 * k * (0.35 + 1.3 * sc[2]))) * (float)Math.Min(1.0, atm.SeaLevelDensity / 1.225 * 4 + 0.25));
        _atmoMat.SetShaderParameter("beta_m", (float)(21e-6 * k * Math.Min(1.0, atm.SeaLevelDensity / 1.225 * 3)));
        _atmoMat.SetShaderParameter("scale_h", (float)atm.ScaleHeight);
    }

    public void SetAtmosphereQuality(int steps) => _atmoMat?.SetShaderParameter("steps", steps);
    public void SetAtmosphereParam(string name, Variant value) => _atmoMat?.SetShaderParameter(name, value);

    // ------------------------------------------------------------------------------------------------------------
    /// <summary>
    /// Update placement and level of detail.
    /// </summary>
    /// <param name="time">Universal time (selects the body's rotation).</param>
    /// <param name="bodyRelOrigin">Body centre relative to the render origin (inertial axes), metres.</param>
    /// <param name="cameraRelBody">Camera position relative to the body centre (inertial axes), metres.</param>
    /// <param name="sunDirWorld">Unit vector from the body to the star.</param>
    public void UpdateView(double time, Vec3d bodyRelOrigin, Vec3d cameraRelBody, Vec3d sunDirWorld)
    {
        _frame++;
        double distToCam = cameraRelBody.Length;
        // Scale about the camera by the distance to the nearest surface point (not the centre), so a vessel standing on the ground is never scaled.
        double s = CompressionScale(Math.Max(1.0, distToCam - Body.Radius));
        RenderScale = s;

        var rotQ = Body.RotationAt(time);
        var q = new Quaternion((float)rotQ.X, (float)rotQ.Y, (float)rotQ.Z, (float)rotQ.W);
        var basis = new Basis(q).Scaled(new Vector3((float)s, (float)s, (float)s));

        if (_simpleSphere != null)
        {
            _simpleSphere.Transform = new Transform3D(new Basis(Quaternion.Identity).Scaled(new Vector3((float)(Body.Radius * s), (float)(Body.Radius * s), (float)(Body.Radius * s))),
                new Vector3((float)(bodyRelOrigin.X * s), (float)(bodyRelOrigin.Y * s), (float)(bodyRelOrigin.Z * s)));
            return;
        }

        var camLocal = rotQ.InverseRotate(cameraRelBody);
        SelectAndDraw(camLocal, basis, bodyRelOrigin, rotQ, s);

        if (_atmoNode != null && _atmoMat != null)
        {
            var atm = Body.Atmosphere!;
            double ar = (Body.Radius + atm.Height) * s;
            var c = new Vector3((float)(bodyRelOrigin.X * s), (float)(bodyRelOrigin.Y * s), (float)(bodyRelOrigin.Z * s));
            _atmoNode.Transform = new Transform3D(new Basis(Quaternion.Identity).Scaled(new Vector3((float)ar, (float)ar, (float)ar) * 1.02f), c);
            _atmoMat.SetShaderParameter("center_world", c);
            _atmoMat.SetShaderParameter("planet_r", (float)(Body.Radius * s));
            _atmoMat.SetShaderParameter("atmo_r", (float)ar);
            _atmoMat.SetShaderParameter("inv_scale", (float)(1.0 / s));
            _atmoMat.SetShaderParameter("sun_dir", new Vector3((float)sunDirWorld.X, (float)sunDirWorld.Y, (float)sunDirWorld.Z));
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    private void SelectAndDraw(Vec3d camLocal, Basis basis, Vec3d bodyRelOrigin, QuatD rot, double s)
    {
        double R = Body.Radius;
        double camR = camLocal.Length;
        var camDir = camLocal / Math.Max(1e-9, camR);
        double maxH = Body.Terrain!.Amplitude * 1.4;
        // angular horizon (with margin for mountains)
        double horizon = camR > R ? Math.Acos(Math.Min(1.0, R / camR)) : Math.PI;
        horizon += Math.Acos(R / (R + maxH));

        _fwdLocal = rot.InverseRotate(CameraForwardWorld);
        var desired = new List<ChunkKey>(256);
        int maxLevel = Math.Min(_maxLevel, MaxLevelCap);
        for (int f = 0; f < 6; f++) Select(new ChunkKey(f, 0, 0, 0), camLocal, camDir, horizon, maxLevel, desired);

        Array.Clear(DesiredByLevel); foreach (var dk in desired) DesiredByLevel[dk.Level]++;
        // Which chunks do we actually draw (fall back to the nearest ready ancestor)
        var toDraw = new HashSet<ChunkKey>();
        var ladder = new HashSet<ChunkKey>();
        var requests = new List<(ChunkKey key, double pri)>();
        foreach (var k in desired)
        {
            var cur = k;
            while (true)
            {
                if (_chunks.TryGetValue(cur, out var ch)) { ch.LastUsed = _frame; if (ch.Node != null) { toDraw.Add(cur); break; } }
                if (cur.Level == 0) break;
                cur = cur.Parent;
            }
            if (!_chunks.TryGetValue(k, out var ex) || (ex.Node == null && !ex.Building))
            {
                var cd = PlanetChunkBuilder.CenterDirection(k) * R;
                requests.Add((k, (camLocal - cd).Length));
            }
            // keep the ancestor chain alive for fallback, and make sure a coarse ladder of ancestors exists so the view never has holes
            for (var a = k; a.Level > 0;)
            {
                a = a.Parent;
                if (_chunks.TryGetValue(a, out var ac)) ac.LastUsed = _frame;
                else if (a.Level % 2 == 0 && ladder.Add(a)) requests.Add((a, -1e12 + a.Level));   // coarse levels are requested first
            }
        }
        // drop drawn chunks that have an ancestor also drawn (avoids overlap / z-fighting while loading)
        var final = new HashSet<ChunkKey>();
        foreach (var k in toDraw)
        {
            bool covered = false;
            for (var a = k; a.Level > 0;) { a = a.Parent; if (toDraw.Contains(a)) { covered = true; break; } }
            if (!covered) final.Add(k);
        }

        // Show/hide + transforms
        foreach (var k in _drawn) if (!final.Contains(k) && _chunks.TryGetValue(k, out var c0) && c0.Node != null) c0.Node.Visible = false;
        _drawn.Clear();
        foreach (var k in final)
        {
            var ch = _chunks[k];
            var o = (bodyRelOrigin + rot.Rotate(ch.Center)) * s;
            ch.Node!.Transform = new Transform3D(basis, new Vector3((float)o.X, (float)o.Y, (float)o.Z));
            ch.Node.Visible = true;
            _drawn.Add(k);
        }

        // Start builds (closest first), harvest finished ones
        _lastRequests = requests.Count;
        requests.Sort((a, b) => a.pri.CompareTo(b.pri));
        foreach (var (key, _) in requests)
        {
            if (_inFlight >= MaxConcurrentBuilds) break;
            if (!_chunks.TryGetValue(key, out var ch)) _chunks[key] = ch = new Chunk { Key = key, LastUsed = _frame };
            if (ch.Building || ch.Node != null) continue;
            ch.Building = true; _inFlight++;
            var body = Body;
            Task.Run(() => { var data = PlanetChunkBuilder.Build(body, key); _finished.Enqueue(data); });
        }
        Harvest(MaxBuildsPerFrame);
        Evict();
    }

    private Vec3d _fwdLocal;
    public readonly int[] DesiredByLevel = new int[24];

    private void Select(ChunkKey k, Vec3d camLocal, Vec3d camDir, double horizon, int maxLevel, List<ChunkKey> outList)
    {
        double R = Body.Radius;
        var cdir = PlanetChunkBuilder.CenterDirection(k);
        double size = PlanetChunkBuilder.ChunkSize(R, k.Level);
        double nodeAng = size / R * 0.85;
        double ang = Math.Acos(Math.Clamp(Vec3d.Dot(cdir, camDir), -1, 1));
        if (ang > horizon + nodeAng) return;                         // behind the horizon
        var center = cdir * R;
        var toNode = center - camLocal;
        double dnode = toNode.Length;
        if (k.Level >= 5 && dnode > size * 1.2)
        {
            double cosA = Vec3d.Dot(toNode / dnode, _fwdLocal);
            double angle = Math.Acos(Math.Clamp(cosA, -1, 1));
            double nodeSeen = Math.Atan(size * 0.8 / dnode);
            if (angle > ViewConeHalfAngle + nodeSeen) return;
        }
        double dist = Math.Max(0, dnode - size * 0.7);
        if (k.Level < maxLevel && dist < size * SplitFactor)
        {
            for (int cx = 0; cx < 2; cx++) for (int cy = 0; cy < 2; cy++) Select(k.Child(cx, cy), camLocal, camDir, horizon, maxLevel, outList);
        }
        else outList.Add(k);
    }

    private void Harvest(int max)
    {
        int n = 0;
        while (n < max && _finished.TryDequeue(out var data)) { Install(data); n++; _inFlight--; }
    }

    private void Install(ChunkData d)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = d.Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = d.Normals;
        arrays[(int)Mesh.ArrayType.Color] = d.Colors;
        arrays[(int)Mesh.ArrayType.TexUV] = d.Uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = d.Uv2;
        arrays[(int)Mesh.ArrayType.Index] = PlanetChunkBuilder.Indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = _terrainMat, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(mi);
        if (!_chunks.TryGetValue(d.Key, out var ch)) _chunks[d.Key] = ch = new Chunk { Key = d.Key };
        ch.Node = mi; ch.Center = d.Center; ch.Building = false; ch.LastUsed = _frame;
    }

    private void Evict()
    {
        if (_chunks.Count < 420) return;
        var victims = _chunks.Values.Where(c => c.Node != null && c.LastUsed < _frame - 90 && !c.Building)
            .OrderBy(c => c.LastUsed).Take(_chunks.Count - 320).ToList();
        foreach (var v in victims)
        {
            v.Node!.QueueFree();
            _chunks.Remove(v.Key);
        }
    }

    /// <summary>Synchronously build everything needed for the current view so the first frame is complete.</summary>
    public void Prewarm(double time, Vec3d bodyRelOrigin, Vec3d cameraRelBody, Vec3d sunDir)
    {
        int oldConc = MaxConcurrentBuilds, oldPer = MaxBuildsPerFrame;
        MaxConcurrentBuilds = 8; MaxBuildsPerFrame = 256;
        for (int pass = 0; pass < 400; pass++)
        {
            UpdateView(time, bodyRelOrigin, cameraRelBody, sunDir);
            if (_lastRequests == 0 && _inFlight <= 0) break;
            int guard = 0;
            while (_inFlight > 0 && guard++ < 2000 && _finished.IsEmpty) System.Threading.Thread.Sleep(1);
        }
        MaxConcurrentBuilds = oldConc; MaxBuildsPerFrame = oldPer;
        UpdateView(time, bodyRelOrigin, cameraRelBody, sunDir);
    }
}
