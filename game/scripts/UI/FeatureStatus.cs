namespace OSP.Game.UI;

/// <summary>Honest feature matrix shown in the UI and mirrored in ROADMAP.md.</summary>
public static class FeatureStatus
{
    public sealed record Item(string Area, string Feature, string State, string Note = "");

    public static readonly Item[] Items =
    {
        new("Vehicle Assembly", "3D part placement with stack/radial/socket nodes, symmetry, undo/redo, hierarchy, staging list", "Working"),
        new("Vehicle Assembly", "Mass, thrust, TWR and per-stage delta-v (vacuum & atmosphere) from the real flight code", "Working"),
        new("Vehicle Assembly", "Save / load / duplicate / rename / delete designs", "Working"),
        new("Vehicle Assembly", "Fairings, cargo bays, fuel lines, struts", "Planned", "Roadmap M3"),
        new("Flight", "Rigid-body physics: thrust, gimbal, drag/lift, gravity, reaction wheels, RCS", "Working"),
        new("Flight", "Staging, decouplers, SRBs, separated stages simulated independently", "Working"),
        new("Flight", "Landing legs, parachutes, terrain collision, crash damage, joint failure", "Working"),
        new("Flight", "Electric power: solar panels, batteries, eclipse", "Working"),
        new("Flight", "Atmospheric heating / re-entry burn-up", "Planned", "Drag and decay work; thermal damage is not modelled"),
        new("Orbit", "Kepler on-rails propagation, time warp, SoI transitions (patched conics)", "Working"),
        new("Orbit", "Orbit prediction, Ap/Pe, maneuver nodes", "Working"),
        new("Orbit", "Maneuver node dragging handles in the map (numeric editor works)", "Partial"),
        new("World", "Home planet Aldara, moon Lumen, planet Cinder, star Vesper; procedural LOD terrain", "Working"),
        new("World", "Further planets, rings, gas giant", "Planned", "Roadmap M4"),
        new("Gameplay", "Docking ports and rendezvous tools", "Planned", "Roadmap M4"),
        new("Gameplay", "Science instruments, crew management, career mode, contracts, tutorials", "Planned", "Roadmap M4-M5"),
        new("Presentation", "Audio", "Planned", "No sound yet (silent build)"),
    };
}
