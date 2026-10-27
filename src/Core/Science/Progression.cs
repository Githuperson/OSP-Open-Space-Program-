using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OSP.Core.Celestial;
using OSP.Core.Flight;
using OSP.Core.Parts;
using OSP.Core.Persistence;

namespace OSP.Core.Science
{
    public enum GameMode
    {
        /// <summary>All parts unlocked, no money.</summary>
        Sandbox,
        /// <summary>Research progression with science points.</summary>
        Science,
        /// <summary>Science plus funding, part costs and contracts.</summary>
        Career,
    }

    public sealed class TechNode
    {
        public string Id { get; init; }
        public string Name { get; init; }
        public string Description { get; init; } = "";
        public double Cost { get; init; }
        public List<string> Requires { get; init; } = new();
    }

    public sealed class TechTree
    {
        public List<TechNode> Nodes { get; init; } = new();
        public TechNode Get(string id) => Nodes.FirstOrDefault(n => n.Id == id);

        public static TechTree FromJson(string json)
        {
            TechTree t = JsonSerializer.Deserialize<TechTree>(json, JsonUtil.Options) ?? throw new FormatException("Empty tech tree.");
            foreach (TechNode n in t.Nodes)
                foreach (string r in n.Requires)
                    if (t.Get(r) == null) throw new FormatException($"Tech '{n.Id}' requires unknown tech '{r}'.");
            if (t.Get("start") == null) throw new FormatException("The tech tree needs a 'start' node.");
            return t;
        }
    }

    public enum ObjectiveType
    {
        ReachAltitude,
        ReachSpace,
        Orbit,
        Land,
        SplashDown,
        EnterSoi,
        ReturnHome,
        CollectScience,
    }

    public sealed class ContractDefinition
    {
        public string Id { get; init; }
        public string Title { get; init; }
        public string Description { get; init; }
        public ObjectiveType Objective { get; init; }
        public string Body { get; init; } = "aster";
        public double Altitude { get; init; }
        public string Experiment { get; init; }
        public double RewardFunds { get; init; }
        public double RewardScience { get; init; }
        public double AdvanceFunds { get; init; }
        /// <summary>Contracts that must be completed before this one is offered.</summary>
        public List<string> Requires { get; init; } = new();
    }

    public sealed class ContractList
    {
        public List<ContractDefinition> Contracts { get; init; } = new();
        public static ContractList FromJson(string json) =>
            JsonSerializer.Deserialize<ContractList>(json, JsonUtil.Options) ?? throw new FormatException("Empty contract list.");
    }

    /// <summary>Persistent progression (science, research, money, contracts). Saved inside <see cref="SaveGame"/>.</summary>
    public sealed class ProgressState
    {
        public GameMode Mode { get; set; } = GameMode.Sandbox;
        public double Funds { get; set; } = 40000;
        public double Science { get; set; }
        public List<string> UnlockedTech { get; set; } = new() { "start" };
        /// <summary>Science already credited per result key.</summary>
        public Dictionary<string, double> CollectedScience { get; set; } = new();
        public List<string> AcceptedContracts { get; set; } = new();
        public List<string> CompletedContracts { get; set; } = new();
        /// <summary>Contract objectives reached in the current flight but not yet completed (e.g. ReturnHome needs recovery).</summary>
        public List<string> Flags { get; set; } = new();

        public void Validate(PartCatalog catalog)
        {
            UnlockedTech ??= new List<string>();
            if (!UnlockedTech.Contains("start")) UnlockedTech.Add("start");
            CollectedScience ??= new Dictionary<string, double>();
            AcceptedContracts ??= new List<string>();
            CompletedContracts ??= new List<string>();
            Flags ??= new List<string>();
            if (!double.IsFinite(Funds)) Funds = 0;
            if (!double.IsFinite(Science) || Science < 0) Science = 0;
        }

        public bool IsPartAvailable(PartDefinition p) => Mode == GameMode.Sandbox || UnlockedTech.Contains(p.Tech ?? "start");

        public bool CanResearch(TechTree tree, TechNode node, out string reason)
        {
            reason = null;
            if (UnlockedTech.Contains(node.Id)) { reason = "Already researched."; return false; }
            if (node.Requires.Any(r => !UnlockedTech.Contains(r))) { reason = "Requires: " + string.Join(", ", node.Requires.Where(r => !UnlockedTech.Contains(r)).Select(r => tree.Get(r)?.Name ?? r)); return false; }
            if (Science < node.Cost) { reason = $"Needs {node.Cost:0} science."; return false; }
            return true;
        }

        public bool Research(TechTree tree, string id)
        {
            TechNode n = tree.Get(id);
            if (n == null || !CanResearch(tree, n, out _)) return false;
            Science -= n.Cost;
            UnlockedTech.Add(id);
            return true;
        }

        /// <summary>
        /// Credits a result with diminishing returns: each submission earns <paramref name="fraction"/> of the value
        /// still remaining for that key. Returns the science gained.
        /// </summary>
        public double Credit(ScienceResult result, CelestialSystem system, double fraction)
        {
            ExperimentDefinition e = ScienceRules.Get(result.ExperimentId);
            CelestialBody b = system.Get(result.BodyId);
            if (e == null || b == null) return 0;
            double cap = ScienceRules.Value(e, b, result.Situation);
            CollectedScience.TryGetValue(result.Key, out double have);
            double gain = System.Math.Max(0, (cap - have) * fraction);
            gain = System.Math.Round(gain, 2);
            if (gain <= 0) return 0;
            CollectedScience[result.Key] = have + gain;
            if (Mode != GameMode.Sandbox) Science += gain;
            return gain;
        }

        public double RemainingValue(ScienceResult result, CelestialSystem system)
        {
            ExperimentDefinition e = ScienceRules.Get(result.ExperimentId);
            CelestialBody b = system.Get(result.BodyId);
            if (e == null || b == null) return 0;
            double cap = ScienceRules.Value(e, b, result.Situation);
            CollectedScience.TryGetValue(result.Key, out double have);
            return System.Math.Max(0, cap - have);
        }

        public IEnumerable<ContractDefinition> OfferedContracts(ContractList list) =>
            list.Contracts.Where(c => !CompletedContracts.Contains(c.Id) && !AcceptedContracts.Contains(c.Id) && c.Requires.All(CompletedContracts.Contains));

        public bool Accept(ContractDefinition c)
        {
            if (AcceptedContracts.Contains(c.Id) || CompletedContracts.Contains(c.Id)) return false;
            AcceptedContracts.Add(c.Id);
            if (Mode == GameMode.Career) Funds += c.AdvanceFunds;
            return true;
        }

        /// <summary>Marks a contract complete and pays its rewards. Returns false if it was not active.</summary>
        public bool Complete(ContractDefinition c)
        {
            if (!AcceptedContracts.Remove(c.Id)) return false;
            CompletedContracts.Add(c.Id);
            if (Mode == GameMode.Career) Funds += c.RewardFunds;
            if (Mode != GameMode.Sandbox) Science += c.RewardScience;
            return true;
        }
    }

    /// <summary>Evaluates contract objectives against the simulated vessel state.</summary>
    public static class ContractEvaluator
    {
        public static bool IsMet(ContractDefinition c, Vessel v, CelestialSystem system, ProgressState progress, bool recovered = false)
        {
            CelestialBody target = system.Get(c.Body) ?? system.HomeBody;
            switch (c.Objective)
            {
                case ObjectiveType.ReachAltitude:
                    return v.Body == target && v.Altitude >= c.Altitude;
                case ObjectiveType.ReachSpace:
                    return v.Body == target && v.Altitude >= System.Math.Max(target.AtmosphereHeight, c.Altitude);
                case ObjectiveType.Orbit:
                    return v.Body == target && v.Situation == Situation.Orbiting;
                case ObjectiveType.Land:
                    return v.Body == target && v.Situation == Situation.Landed;
                case ObjectiveType.SplashDown:
                    return v.Body == target && v.Situation == Situation.Splashed;
                case ObjectiveType.EnterSoi:
                    return v.Body == target;
                case ObjectiveType.ReturnHome:
                    return recovered && progress.Flags.Contains("visited:" + c.Body);
                case ObjectiveType.CollectScience:
                    return progress.CollectedScience.Keys.Any(k => k.StartsWith(c.Experiment + "@" + c.Body, StringComparison.Ordinal));
                default:
                    return false;
            }
        }
    }

    /// <summary>Career money rules.</summary>
    public static class CareerRules
    {
        /// <summary>Recovered parts return their cost scaled by distance from the launch site (98% on the pad, down to 60%).</summary>
        public static double RecoveryFactor(double distanceFromSiteMeters)
        {
            double km = distanceFromSiteMeters / 1000;
            return System.Math.Clamp(0.98 - km / 2000.0, 0.6, 0.98);
        }
    }
}
