# Simulation model

Units: SI throughout (m, kg, s, N, Pa, kg/s). Angles in radians internally, degrees in data files.

## Frames and precision
- Every vessel state is a double-precision `Vector3d` relative to the centre of its SOI body, in a non-rotating frame. The frame is right-handed and +Y points to the body's north pole.
- Bodies follow fixed Keplerian orbits around their parents and rotate about +Y.
- Rendering uses a **floating origin**: the active vessel's centre of mass is at (0,0,0) every frame, and everything is converted to float only after subtracting the origin.
- Distant bodies are drawn in a separate **scaled-space** world at 1:1000, composited behind the near world through the sky shader. Objects beyond 250,000 units are pulled towards the camera and shrunk by the same factor, which preserves their angular size. This keeps the camera depth ratio within the range Godot can build frustums for.

## Integrators
- **Translation (physics):** fixed 20 ms steps, split into 5 ms sub-steps near the ground. Each step is RK4 with gravity evaluated at all four stages, while thrust, aerodynamic and contact forces are held constant over the step. A 100 km circular orbit integrated for a full period drifts less than 1 m in semi-major axis (see the tests).
- **Rotation:** semi-implicit Euler on ω with the full world-space inertia tensor and the gyroscopic term ω × Iω, followed by an exact quaternion update.
- **Rails (time warp / unloaded vessels):** analytic universal-variable Kepler propagation (Curtis Alg. 3.3/3.4). It is exact to floating point for elliptic, parabolic and hyperbolic orbits, so warping cannot drift an orbit.
- **Limitation:** forces are piecewise constant within a step. Very stiff contacts are handled by penalty springs scaled to vessel mass.

## Gravity and patched conics
- Gravity is g = −μ r/|r|³ from the SOI body only.
- SOI radius is the Laplace radius a(m/M)^0.4.
- Escape times are found exactly from the conic. Encounters are found by sampling the vessel–moon distance at steps of 0.3 SOI of relative motion, then bisecting.
- At a transition, the state is re-expressed relative to the new body using the bodies' exact positions and velocities.

## Propulsion
- Mass flow: ṁ = F_vac / (Isp_vac·g0), scaled by throttle.
- Thrust: F = ṁ·Isp(p)·g0, where Isp is interpolated linearly in pressure between the vacuum and sea-level values. Sea-level thrust is therefore lower than vacuum thrust.
- Propellants are drawn in their mass ratios. A shortfall reduces thrust proportionally (flame-out).
- Solid motors cannot throttle or shut down.
- Gimbals tilt thrust towards the commanded torque.

## Resources
- Fuel and oxidiser flow through attached parts, but not across a decoupler-to-parent joint.
- Fuel lines let the target group drain the source group first (asparagus staging).
- Electricity and monopropellant are vessel-wide. Solid fuel never leaves its part.

## Aerodynamics
- Atmosphere: isothermal exponential, p = p0·e^(−h/H) and ρ = ρ0·e^(−h/H), cut off at the atmosphere height.
- Part drag: F = −½ρ|u|u·A·Cd for each local axis. Stack faces are shielded by neighbouring parts.
- Fins use a flat-plate normal-force model (slope 2π, with stall). Control surfaces deflect towards the commanded torque.
- Parachutes: drag area CdA, with semi-deployed and fully deployed states. They tear above a dynamic-pressure limit.
- Heating is not simulated yet.

## Contact and damage
- Hull contact points sit on each part's bounding cylinder or box. Contacts are mass-scaled spring-dampers (3 Hz hull, 1.2 Hz water, leg-specific suspension) with Coulomb friction.
- An impact faster than the part's crash tolerance destroys the part.
- Joint loads: F = Σ_subtree (Fᵢ − mᵢ(a + α×rᵢ + ω×(ω×rᵢ))). A joint whose load exceeds the breaking force for 3 consecutive steps splits the vessel.

## Staging and delta-v
- Stage 0 fires first. Decouplers stay with the part below them and push the two halves apart with equal and opposite impulses.
- Δv is estimated by simulating the staging sequence with the real resource model, using Δv = vₑ·ln(m0/m1) per step.

## Control
- SAS is a rate-limited attitude controller. The desired rate is ω = min(√(1.2·α·θ), 2.5θ, 0.6 rad/s), and the torque command is (ω_des − ω)·4/α per axis.
- Vessel frame: nose +Y, right +X, dorsal +Z.
