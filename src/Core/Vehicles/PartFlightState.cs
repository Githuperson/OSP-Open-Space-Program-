using System.Collections.Generic;

namespace OSP.Core.Vehicles
{
    public enum ParachuteState
    {
        Stowed,
        /// <summary>Staged; waiting for sufficient pressure to semi-deploy.</summary>
        Armed,
        SemiDeployed,
        Deployed,
        Cut,
    }

    /// <summary>Persistent per-part module state during flight. Serialised in save games.</summary>
    public sealed class PartFlightState
    {
        /// <summary>Engines: ignited. Decouplers/jettisons: fired.</summary>
        public bool Activated { get; set; }
        public bool Jettisoned { get; set; }
        /// <summary>Engines: shut down by the player (liquid engines only).</summary>
        public bool Shutdown { get; set; }
        /// <summary>Deploy progress for animated modules (0 = stowed, 1 = deployed).</summary>
        public double Deploy { get; set; }
        /// <summary>Requested deploy state (legs, panels).</summary>
        public bool DeployTarget { get; set; }
        public ParachuteState Parachute { get; set; } = ParachuteState.Stowed;
        /// <summary>Broken solar panel / torn parachute.</summary>
        public bool Broken { get; set; }
        /// <summary>Current (spooled) engine throttle 0..1.</summary>
        public double EngineThrottle { get; set; }
        /// <summary>Crew member names seated in this part.</summary>
        public List<string> Crew { get; set; } = new();
        /// <summary>Experiment ids whose results are stored in this part (data not yet transmitted or recovered).</summary>
        public List<string> StoredScience { get; set; } = new();

        public PartFlightState Clone()
        {
            var c = (PartFlightState)MemberwiseClone();
            c.Crew = new List<string>(Crew);
            c.StoredScience = new List<string>(StoredScience);
            return c;
        }
    }
}
