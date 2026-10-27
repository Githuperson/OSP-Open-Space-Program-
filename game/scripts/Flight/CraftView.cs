using System.Collections.Generic;
using Godot;
using OSP.Core.Flight;
using OSP.Core.Mathx;
using OSP.Game.Rendering;

namespace OSP.Game.Flight;

/// <summary>Scene-graph representation of one <see cref="Craft"/>: part models at their current offsets from the CoM, engine plumes, deploy animations.</summary>
public sealed partial class CraftView : Node3D
{
    public Craft Craft { get; }
    private readonly Dictionary<int, PartModel> _models = new();
    private readonly Dictionary<int, PlumeView> _plumes = new();
    private int _partCount = -1;

    public CraftView(Craft craft) { Craft = craft; Name = $"Craft_{craft.Id}"; Rebuild(); }

    private void Rebuild()
    {
        foreach (var m in _models.Values) m.QueueFree();
        _models.Clear(); _plumes.Clear();
        foreach (var p in Craft.Parts)
        {
            var model = PartModel.Build(p.Def);
            model.Quaternion = new Quaternion((float)p.Rotation.X, (float)p.Rotation.Y, (float)p.Rotation.Z, (float)p.Rotation.W);
            AddChild(model);
            _models[p.Id] = model;
            if (p.Def.Engine is { } e)
            {
                var plume = new PlumeView(e);
                var n = e.NozzleV;
                plume.Position = new Vector3((float)n.X, (float)n.Y, (float)n.Z);
                model.AddChild(plume);
                _plumes[p.Id] = plume;
            }
        }
        _partCount = Craft.Parts.Count;
    }

    /// <summary>Update the visuals. <paramref name="pressureAtm"/> drives plume shape.</summary>
    public void Refresh(double pressureAtm)
    {
        if (Craft.Parts.Count != _partCount) Rebuild();
        var com = Craft.Mass.CenterOfMass;
        foreach (var p in Craft.Parts)
        {
            if (!_models.TryGetValue(p.Id, out var m)) continue;
            var d = p.Position - com;
            m.Position = new Vector3((float)d.X, (float)d.Y, (float)d.Z);
            if (p.Def.Leg != null || p.Def.Solar != null || p.Def.Parachute != null)
            {
                bool open = p.Chute == ChuteState.Semi || p.Chute == ChuteState.Deployed;
                double scale = p.Chute == ChuteState.Semi ? 0.25 + 0.4 * p.ChuteProgress : p.Chute == ChuteState.Deployed ? 0.65 + 0.35 * p.ChuteProgress : 0.05;
                m.Animate(p.LegDeploy, p.SolarDeploy, open, scale);
            }
            if (_plumes.TryGetValue(p.Id, out var pl)) pl.SetState(p.Ignited ? p.PlumeIntensity : 0, pressureAtm);
        }
    }
}
