using System.Collections.Generic;

namespace OSP.Core.Parts
{
    /// <summary>How a resource may move between parts.</summary>
    public enum ResourceFlowMode
    {
        /// <summary>Flows through attached parts but is blocked by decouplers unless a fuel line bridges them.</summary>
        StackCrossfeed,
        /// <summary>Available to every part of the vessel (e.g. electricity, monopropellant).</summary>
        AllVessel,
        /// <summary>Cannot leave the part that contains it (e.g. solid propellant).</summary>
        NoFlow,
    }

    public sealed class ResourceDefinition
    {
        public string Id { get; init; }
        public string Name { get; init; }
        /// <summary>Mass of one unit in kg. Propellants are measured in kilograms (density 1).</summary>
        public double Density { get; init; }
        public ResourceFlowMode Flow { get; init; }
        public string Unit { get; init; } = "kg";
    }

    /// <summary>The built-in resource set. Propellant amounts are in kg; electric charge in EC (1 EC ≈ 1 kJ).</summary>
    public static class ResourceLibrary
    {
        public const string Fuel = "Fuel";
        public const string Oxidizer = "Oxidizer";
        public const string SolidFuel = "SolidFuel";
        public const string Monopropellant = "Monopropellant";
        public const string ElectricCharge = "ElectricCharge";

        private static readonly Dictionary<string, ResourceDefinition> Defs = new()
        {
            [Fuel] = new ResourceDefinition { Id = Fuel, Name = "Kerolox Fuel", Density = 1, Flow = ResourceFlowMode.StackCrossfeed },
            [Oxidizer] = new ResourceDefinition { Id = Oxidizer, Name = "Liquid Oxidizer", Density = 1, Flow = ResourceFlowMode.StackCrossfeed },
            [SolidFuel] = new ResourceDefinition { Id = SolidFuel, Name = "Solid Propellant", Density = 1, Flow = ResourceFlowMode.NoFlow },
            [Monopropellant] = new ResourceDefinition { Id = Monopropellant, Name = "Monopropellant", Density = 1, Flow = ResourceFlowMode.AllVessel },
            [ElectricCharge] = new ResourceDefinition { Id = ElectricCharge, Name = "Electric Charge", Density = 0, Flow = ResourceFlowMode.AllVessel, Unit = "EC" },
        };

        public static ResourceDefinition Get(string id) => Defs.TryGetValue(id, out var d) ? d : null;
        public static IEnumerable<ResourceDefinition> All => Defs.Values;
        public static bool Exists(string id) => Defs.ContainsKey(id);
    }
}
