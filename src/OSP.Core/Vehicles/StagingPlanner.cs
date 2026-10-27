using OSP.Core.Parts;

namespace OSP.Core.Vehicles;

/// <summary>
/// Default staging assignment (stage numbers count down: the highest number fires first, at launch).
///
/// Rules (deterministic, documented in docs/PHYSICS.md):
///  * k(D) = number of decouplers on the path root -> D, inclusive.  Decoupler D fires in stage k(D)-1.
///  * Launch stage L = max k over all decouplers (0 if there are none).
///  * An engine fires with the nearest decoupler *below it in its own stack chain* (so an upper-stage engine
///    ignites when the stage beneath it is jettisoned); an engine with no decoupler below it ignites at launch (L).
///  * Solid boosters and core engines on a vehicle with radial decouplers therefore all light at launch.
/// Parachutes, legs, solar panels etc. are not staged (they have their own action keys).
/// </summary>
public static class StagingPlanner
{
    public static void AutoAssign(VehicleDesign d, PartCatalog catalog)
    {
        foreach (var p in d.Parts) p.Stage = -1;
        if (d.IsEmpty) return;
        var byId = d.Parts.ToDictionary(p => p.Id);

        int DecouplerDepth(PartInstance p)
        {
            int k = 0;
            for (var cur = p; cur != null; cur = cur.ParentId >= 0 ? byId[cur.ParentId] : null)
                if (catalog.Get(cur.PartId).Decoupler != null) k++;
            return k;
        }

        int launch = 0;
        foreach (var p in d.Parts)
            if (catalog.Get(p.PartId).Decoupler != null) launch = Math.Max(launch, DecouplerDepth(p));

        var children = d.Parts.Where(p => p.ParentId >= 0).GroupBy(p => p.ParentId).ToDictionary(g => g.Key, g => g.ToList());

        // Nearest decoupler in the stack chain below an engine = first decoupler reachable by walking
        // through its stack-attached descendants.
        int? NearestDecouplerBelow(PartInstance p)
        {
            var q = new Queue<PartInstance>();
            q.Enqueue(p);
            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                if (!children.TryGetValue(cur.Id, out var kids)) continue;
                foreach (var k in kids)
                {
                    if (k.Mode != AttachMode.Stack) continue;
                    var kd = catalog.Get(k.PartId);
                    if (kd.Decoupler != null) return DecouplerDepth(k) - 1;
                    q.Enqueue(k);
                }
            }
            return null;
        }

        foreach (var p in d.Parts)
        {
            var def = catalog.Get(p.PartId);
            if (def.Decoupler != null) p.Stage = DecouplerDepth(p) - 1;
            else if (def.Engine != null) p.Stage = NearestDecouplerBelow(p) ?? launch;
        }
    }

    public static int MaxStage(VehicleDesign d) => d.Parts.Count == 0 ? -1 : d.Parts.Max(p => p.Stage);
}
