using OSP.Core.Parts;

namespace OSP.Core.Vehicles;

public enum IssueSeverity { Info, Warning, Error }

public sealed record ValidationIssue(IssueSeverity Severity, string Message, int PartId = -1);

/// <summary>Structural and gameplay validation of a vehicle design. Errors block launch; warnings do not.</summary>
public static class VehicleValidator
{
    public static List<ValidationIssue> Validate(VehicleDesign d, PartCatalog catalog)
    {
        var issues = new List<ValidationIssue>();
        if (d.IsEmpty) { issues.Add(new(IssueSeverity.Error, "The vehicle has no parts.")); return issues; }

        var ids = new HashSet<int>();
        foreach (var p in d.Parts)
        {
            if (!ids.Add(p.Id)) issues.Add(new(IssueSeverity.Error, $"Duplicate part id {p.Id}", p.Id));
            if (!catalog.TryGet(p.PartId, out _)) issues.Add(new(IssueSeverity.Error, $"Unknown part '{p.PartId}'", p.Id));
        }
        if (issues.Any(i => i.Severity == IssueSeverity.Error)) return issues;

        if (d.Parts.Count(p => p.ParentId < 0) != 1)
            issues.Add(new(IssueSeverity.Error, "A vehicle must have exactly one root part."));
        foreach (var p in d.Parts)
            if (p.ParentId >= 0 && !ids.Contains(p.ParentId))
                issues.Add(new(IssueSeverity.Error, "Part references a missing parent.", p.Id));
        if (issues.Count > 0) return issues;

        // Node occupancy: each stack/socket node may carry at most one neighbour.
        var used = new HashSet<(int, string)>();
        foreach (var p in d.Parts)
        {
            if (p.ParentId < 0) continue;
            var pdef = catalog.Get(d.Get(p.ParentId).PartId);
            var cdef = catalog.Get(p.PartId);
            var cnode = cdef.Node(p.ChildNode);
            if (cnode == null) { issues.Add(new(IssueSeverity.Error, $"{cdef.Name}: unknown node '{p.ChildNode}'", p.Id)); continue; }
            if (p.Mode == AttachMode.Radial)
            {
                if (!pdef.AcceptsRadial || pdef.Shape.IsBox) issues.Add(new(IssueSeverity.Error, $"{pdef.Name} does not accept radially-mounted parts.", p.Id));
                if (!cnode.IsSurface) issues.Add(new(IssueSeverity.Error, $"{cdef.Name} cannot be surface-mounted.", p.Id));
            }
            else
            {
                var pnode = pdef.Node(p.ParentNode);
                if (pnode == null) { issues.Add(new(IssueSeverity.Error, $"{pdef.Name}: unknown node '{p.ParentNode}'", p.Id)); continue; }
                if (!AttachGeometry.CanMate(pnode, cnode)) issues.Add(new(IssueSeverity.Error, $"{cdef.Name}/{p.ChildNode} cannot connect to {pdef.Name}/{p.ParentNode} (size mismatch).", p.Id));
                if (!used.Add((p.ParentId, p.ParentNode))) issues.Add(new(IssueSeverity.Error, $"{pdef.Name} node '{p.ParentNode}' is used twice.", p.Id));
            }
        }
        if (issues.Count > 0) return issues;

        try { d.Recompute(catalog); }
        catch (Util.DataException ex) { issues.Add(new(IssueSeverity.Error, ex.Message)); return issues; }

        bool command = d.Parts.Any(p => catalog.Get(p.PartId).Command != null);
        if (!command) issues.Add(new(IssueSeverity.Warning, "No command pod or probe core: the vehicle cannot be controlled."));
        bool engine = d.Parts.Any(p => catalog.Get(p.PartId).Engine != null);
        if (!engine) issues.Add(new(IssueSeverity.Warning, "No engines."));
        foreach (var p in d.Parts)
        {
            var def = catalog.Get(p.PartId);
            if (def.Engine != null && p.Stage < 0)
                issues.Add(new(IssueSeverity.Warning, $"{def.Name} is not assigned to a stage and will never ignite.", p.Id));
        }
        // Simple overlap check between non-adjacent parts' bounding spheres is intentionally omitted (documented limitation).
        return issues;
    }
}
