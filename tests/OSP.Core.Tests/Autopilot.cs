using OSP.Core.Bodies;
using OSP.Core.Flight;
using OSP.Core.Mathx;
using OSP.Core.Orbits;
using OSP.Core.Vehicles;

namespace OSP.Core.Tests;

/// <summary>Scripted ascent used by the integration test: vertical rise, gravity turn east, coast, circularise.</summary>
public sealed class Autopilot
{
    public enum Phase { Vertical, Turn, Coast, Circularize, Done }
    public Phase Current = Phase.Vertical;
    public double TargetApoapsis = 90_000;
    public double KickAltitude = 800;
    public double KickDegrees = 4.0;
    public bool Kicked;
    private readonly FlightSimulation _sim;
    private readonly Craft _craft;
    public double Elapsed;
    public List<string> Log = new();

    public Autopilot(FlightSimulation sim, Craft craft) { _sim = sim; _craft = craft; }

    private (double ap, double pe) ApPe()
    {
        var o = Orbit.FromState(_craft.Position, _craft.Velocity, _craft.Body.Mu, _sim.Time);
        return (o.ApoapsisRadius - _craft.Body.Radius, o.PeriapsisRadius - _craft.Body.Radius);
    }

    public void Update(double dt)
    {
        Elapsed += dt;
        var c = _craft; var ctl = c.Control;
        var env = FlightEnvironment.Compute(_sim.System, c.Body, _sim.Time, c.Position, c.Velocity);
        double alt = env.AltitudeSeaLevel;
        var (ap, pe) = ApPe();

        // auto-stage when the active stage has run dry
        bool anyBurning = c.Parts.Any(p => p.Ignited && p.Def.Engine != null);
        if (!anyBurning && ctl.Throttle > 0 && StagingController.NextStage(c) >= 0 && Current != Phase.Done)
        {
            Log.Add($"t={Elapsed:F0} stage at alt {alt:F0} v={c.Velocity.Length:F0}");
            _sim.ActivateStage();
        }

        switch (Current)
        {
            case Phase.Vertical:
                ctl.Throttle = 1;
                if (alt > KickAltitude && !Kicked)
                {
                    var up = env.Up;
                    var east = Vec3d.Cross(Vec3d.UnitY, up).Normalized;
                    var tilted = (up * Math.Cos(KickDegrees * MathUtil.Deg2Rad) + east * Math.Sin(KickDegrees * MathUtil.Deg2Rad)).Normalized;
                    ctl.HoldOrientation = QuatD.FromTo(c.Orientation.Rotate(Vec3d.UnitY), tilted) * c.Orientation;
                    ctl.HoldValid = true; Kicked = true;
                    Log.Add($"t={Elapsed:F0} kick at {alt:F0} m");
                }
                if (Kicked && alt > KickAltitude + 800)
                {
                    ctl.Sas = SasMode.Prograde; Current = Phase.Turn; Log.Add($"t={Elapsed:F0} prograde hold, alt={alt:F0}");
                }
                break;
            case Phase.Turn:
                ctl.Sas = SasMode.Prograde; ctl.Throttle = 1;
                if (ap >= TargetApoapsis) { ctl.Throttle = 0; Current = Phase.Coast; Log.Add($"t={Elapsed:F0} MECO alt={alt:F0} ap={ap:F0}"); }
                break;
            case Phase.Coast:
                ctl.Sas = SasMode.Prograde; ctl.Throttle = 0;
                // keep apoapsis up while still in the atmosphere
                if (alt < 70_000 && ap < TargetApoapsis) ctl.Throttle = 0.5;
                var orb = Orbit.FromState(c.Position, c.Velocity, c.Body.Mu, _sim.Time);
                double tAp = orb.TimeToApoapsis(_sim.Time);
                double dvNeeded = OrbitMath.CircularSpeed(c.Body.Mu, orb.ApoapsisRadius) - OrbitMath.VisViva(c.Body.Mu, orb.ApoapsisRadius, orb.SemiMajorAxis);
                var (bt, ok) = ManeuverPlanner.EstimateBurnTime(c, dvNeeded);
                if (alt > 70_000 && ok && tAp < bt / 2 + 1) { Current = Phase.Circularize; Log.Add($"t={Elapsed:F0} circularize start, dv={dvNeeded:F0}, burn={bt:F0}s"); }
                break;
            case Phase.Circularize:
                ctl.Sas = SasMode.Prograde; ctl.Throttle = 1;
                if (pe > 75_000 || (pe > 70_000 && ap > pe + 30_000 && false)) { ctl.Throttle = 0; Current = Phase.Done; Log.Add($"t={Elapsed:F0} orbit achieved ap={ap:F0} pe={pe:F0}"); }
                break;
            case Phase.Done:
                ctl.Throttle = 0;
                break;
        }
    }
}
