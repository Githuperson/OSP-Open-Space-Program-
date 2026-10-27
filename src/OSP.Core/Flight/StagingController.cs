using OSP.Core.Mathx;
using OSP.Core.Parts;

namespace OSP.Core.Flight;

/// <summary>
/// Stage activation. Stages count down; activating stage N ignites engines and fires decouplers whose Stage == N, in a
/// fixed deterministic order (decouplers by part id first, then engines by part id), then moves on to the next lower
/// stage that contains parts. Decoupler separation applies equal and opposite impulses (momentum conserving).
/// </summary>
public static class StagingController
{
    public sealed class Result
    {
        public int StageFired;
        public List<Craft> NewCrafts { get; } = new();
        public bool NothingToDo;
    }

    /// <summary>Highest stage number still containing an unfired staged part, or -1.</summary>
    public static int NextStage(Craft craft)
    {
        int best = -1;
        foreach (var p in craft.Parts)
        {
            if (p.Stage < 0) continue;
            if (p.Def.Decoupler != null || (p.Def.Engine != null && !p.Ignited && !p.FlamedOut))
                if (p.Stage <= craft.CurrentStage) best = Math.Max(best, p.Stage);
        }
        return best;
    }

    public static Result Activate(Craft craft, double time, Func<int> allocateCraftId)
    {
        var res = new Result();
        int stage = NextStage(craft);
        if (stage < 0) { res.NothingToDo = true; craft.Emit(SimEventKind.Info, "No more stages", time); return res; }
        res.StageFired = stage;
        craft.CurrentStage = stage - 1;
        craft.Emit(SimEventKind.StageActivated, $"Stage {stage} activated", time);

        // Decouplers first so separated engines cannot ignite on the departing piece unless they are staged with it.
        var decouplers = craft.Parts.Where(p => p.Stage == stage && p.Def.Decoupler != null).OrderBy(p => p.Id).ToList();
        foreach (var d in decouplers)
        {
            if (craft.Part(d.Id) == null) continue;     // already left with an earlier separation
            var nc = Decouple(craft, d, time, allocateCraftId);
            if (nc != null) res.NewCrafts.Add(nc);
        }
        foreach (var e in craft.Parts.Where(p => p.Stage == stage && p.Def.Engine != null).OrderBy(p => p.Id).ToList())
        {
            if (e.FlamedOut) continue;
            e.Ignited = true;
            craft.Emit(SimEventKind.EngineIgnition, $"{e.Def.Name} ignition", time, e);
        }
        craft.MassDirty = true;
        return res;
    }

    /// <summary>
    /// Sever the connection at the decoupler's cut node. The side not containing the craft root leaves as a new craft.
    /// </summary>
    public static Craft? Decouple(Craft craft, CraftPart d, double time, Func<int> allocateCraftId)
    {
        var mod = d.Def.Decoupler!;
        var node = d.Def.Node(mod.CutNode)!;
        var nodeDirCraft = d.Rotation.Rotate(node.DirectionV);

        // Which link does the cut node correspond to?
        int detachId = -1;
        if (d.ParentId >= 0 && d.ChildNodeId == mod.CutNode) detachId = d.Id;               // cut the link to my parent: I leave
        else
        {
            foreach (var c in craft.ChildrenOf(d.Id))
                if (c.ParentNodeId == mod.CutNode || (c.Mode == Vehicles.AttachMode.Radial && mod.CutNode == "srf")) { detachId = c.Id; break; }
        }
        if (detachId < 0)
        {
            craft.Emit(SimEventKind.Info, $"{d.Def.Name} fired with nothing attached to separate", time, d);
            return null;
        }

        double mBefore = craft.TotalMass;
        var nc = craft.Detach(detachId);
        nc.Id = allocateCraftId();
        nc.Name = craft.Name + " Stage";
        nc.Control.SasEnabled = false;
        nc.Control.Throttle = 0;

        // Equal and opposite impulse along the node direction (pointing toward the departing side if the child leaves).
        double J = mod.SeparationImpulse;
        var dirCraft = detachId == d.Id ? -nodeDirCraft : nodeDirCraft;       // direction from remaining toward departing piece
        // When the decoupler itself leaves, the node axis points toward the remaining parent; flip it.
        var dirWorld = craft.Orientation.Rotate(dirCraft).Normalized;
        double mA = craft.TotalMass, mB = nc.TotalMass;
        craft.Velocity -= dirWorld * (J / mA);
        nc.Velocity += dirWorld * (J / mB);
        nc.Mode = CraftMode.Physics;
        craft.Mode = CraftMode.Physics;
        craft.Emit(SimEventKind.Separation, $"{d.Def.Name} separated {nc.Parts.Count} part(s)", time, d);
        return nc;
    }
}
