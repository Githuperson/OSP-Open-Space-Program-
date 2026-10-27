# Physics and simulation reference

All quantities are SI. Equations here are exactly what the code implements; tests verify them against analytic cases.

## Coordinate frames
* **Inertial body frame** (per SoI body): origin at the body centre, axes fixed in inertial space. +Y is the north/spin axis, the ecliptic is XZ, reference direction +X. Right-handed.
* **Body-fixed frame**: rotates about +Y with angular speed ω = 2π/T. East is the direction of rotation; lat/lon → direction `(cos φ cos λ, sin φ, −cos φ sin λ)`.
* **Craft frame**: origin at the design's root part. **+Y = nose**, **+Z = heading** (the direction "pitch down" moves the nose toward), **+X = left** when facing +Z. A rocket stands on the pad with +Y up and +Z east. Control sign conventions: pitch +1 → rotation about +X (nose toward +Z), yaw +1 → rotation about +Z (nose to the right), roll +1 → rotation about +Y (clockwise seen from behind).
* Conversion between the star frame and any body frame: `r' = r + r_body(t)`, `v' = v + v_body(t)` (`StarSystem.ConvertFrame`).

## Gravity and orbital mechanics
* Gravity: `a = −μ r / |r|³` (central body only; patched conics).
* Circular speed `√(μ/r)`; escape speed `√(2μ/r)`; vis-viva `v² = μ(2/r − 1/a)`; period `T = 2π √(a³/μ)`; Laplace SoI `r_SoI = a (m/M)^{2/5}` (computed from the data, `Lumen`'s is ≈ 2.3 Mm).
* **Orbit elements ↔ state** (`Orbit`): standard conversions in the textbook frame (Z north) mapped to the game frame by `(x, y, z) → (x, z, −y)`. Elliptic and hyperbolic anomalies solved by Newton iteration (tolerance 1e-14). Near-parabolic orbits are nudged to a tiny negative energy for display.
* **On-rails propagation** (`KeplerPropagator`): universal-variable formulation with Stumpff functions and Laguerre–Conway iteration; one code path for ellipses, parabolas and hyperbolas; whole periods are removed first so precision is independent of step size. Verified: energy and angular momentum conserved to 1e-9 over 2000 steps of 1667 s (= 100 000× warp at 60 fps), full period returns to start, hyperbolic excess speed preserved.
* **Sphere-of-influence transitions** (`FlightSimulation.CheckSoi`): leaving when `|r| > r_SoI` (convert to the parent frame), entering a child when `|r − r_child(t)| < r_SoI,child` (convert to the child frame). Rails sub-steps are limited so a small SoI cannot be skipped over: `Δt ≤ 0.15·gap/speed` for each boundary.
* **Trajectory prediction** (`TrajectoryPredictor`): patched conics. Each patch is a Kepler orbit in one SoI; the next event is the earliest of: manoeuvre node, SoI exit (closed form `r = p/(1+e cos ν)`), surface impact (same closed form), or child SoI entry (sampled over the patch then bisected to 1e-9 s). At events the state is converted to the new frame (or the node's Δv applied) and the next patch begins; up to 6 patches.
* **Manoeuvre nodes**: Δv in the orbital frame at the node time: prograde `v̂`, normal `(r×v)̂`, radial-out `prograde × normal`. Burn time from the rocket equation with the ready engines: `t = (m₀ − m₀ e^{−Δv/(Isp g₀)}) / ṁ`.

### Integrator choices and limits
| Situation | Method | Accuracy / limitation |
|---|---|---|
| Powered, atmospheric, contact | Fixed step 0.02 s. Translation: **kick–drift–kick leapfrog** for gravity (symplectic, 2nd order) with non-gravitational acceleration applied in the first half-kick (1st order in velocity-dependent forces). Rotation: explicit `I ω̇ = τ − ω × Iω` with exact axis-angle orientation update. | Energy drift of a coasting orbit at 4× physics warp is < 1e-5 per orbit (tested). Large angular rates (> ~10 rad/s) about the intermediate axis can lose accuracy. |
| Coasting in vacuum at warp ≥ 5× | Analytic Kepler | Exact for the two-body problem; no perturbations (by design for patched conics). |
| Time warp | 1,2,3,4× use physics sub-steps (max 10 steps/frame, excess time is dropped, never spiralling); 5×…100 000× use rails. Rails require: no thrust, not in atmosphere, not in contact, altitude check (5 km above airless surfaces). Rails advance is truncated 10 s before a predicted atmosphere/surface hazard and warp drops to 1×. |

### Known orbital-model limitations
Point-mass gravity, no n-body or oblateness; manoeuvre execution is manual (no auto-burn); closest-approach/rendezvous tools are not implemented.

## Rocket propulsion
* Mass flow at full throttle: `ṁ = F_vac / (Isp_vac g₀)`, `g₀ = 9.80665`.
* `Isp(p) = Isp_vac + (Isp_atm − Isp_vac)·min(p/1 atm, 1)`, thrust `F = ṁ·throttle·Isp(p)·g₀`. Thrust therefore rises as the rocket climbs (engines differ: e.g. Whisper-V 345 s vac / 85 s ASL, Ember-1 295/262 s).
* Solid motors: fixed full throttle, cannot be shut down; the engine consumes `SolidFuel` from its own tank only.
* Liquid engines draw `Propellant` (a combined fuel+oxidiser mass, kg) evenly from every connected tank (see Resource flow). When nothing is left the engine flames out; thrust scales by the fraction of requested propellant actually obtained in the final step.
* **Gimbal**: the maximum thrust-force direction to create torque `c` is `∝ c × r` (since `τ·c = F·(c × r)`); the nozzle offset vector slews at 0.8 rad/s up to `sin(range)`.
* Total vehicle mass, centre of mass and the full inertia tensor (parallel-axis theorem; cylinders/boxes) are recomputed whenever propellant moves; the state point is shifted so that every part stays fixed in space when the CoM moves.

## Resource flow
Propellant flows between parts connected by stack or radial attachment, **except through parts with `blocksFlow` (decouplers)**. Each engine draws from every reachable tank in equal shares (iteratively re-sharing when a tank empties). Electric charge is a vessel-wide pool. Fuel lines and priority drains are planned (ROADMAP).

## Staging
* Stage numbers count down; `Space` activates the highest stage that still has an unfired staged part. Within a stage: decouplers fire in part-id order, then engines ignite.
* Default assignment (`StagingPlanner`): for a decoupler with `k` decouplers on its path from the root (inclusive) the stage is `k−1`; launch stage `L = max k`; an engine fires with the nearest decoupler below it in its own stack chain, otherwise at launch. Editable in the assembly building.
* **Separation**: the tree is cut at the decoupler's cut node; the side not containing the root becomes a new `Craft` inheriting the rigid-body velocity of its own CoM (`v + ω × Δr`). A separation impulse `J` is applied equal and opposite along the node axis (`Δv₁ = J/m₁`, `Δv₂ = J/m₂`), so linear momentum is conserved exactly (unit-tested). The new craft keeps the same craft-frame coordinates; mass properties are recomputed.

## Aerodynamics
Atmosphere: `p(h) = p₀ e^{−h/H}`, `ρ(h) = ρ₀ e^{−h/H}` for `h < h_top`, else 0; temperature `max(T_min, T₀ − Γ h)`, speed of sound `√(γ R T)`.
Each part experiences, at its centre, with `q = ½ρ|v|²`, `v` = part velocity relative to the co-rotating air (including ω × r):
* exposed end faces (faces covered by a stack neighbour are removed, so a stack has no phantom drag): windward `F = −q C_d A (v̂·n̂)² n̂`, lee-side suction `+q C_base A (v̂·n̂)² n̂`;
* cross-flow on the side area: `F = −q C_side A_side sin²α n̂_⊥`;
* fins: flat-plate normal force `C_N = C_max tanh(2π sin α / C_max)`, `C_max = 1.3`, plus skin friction; control surfaces rotate the plate by up to ±25° to realise the commanded torque;
* parachutes: `F = −q (C_d A)(t) v̂`, `C_dA` ramps through semi- and full deployment; deployment requires pressure ≥ the chute's minimum, full opening below its altitude, and a canopy that sees `q` above its limit is torn away;
* all part drag is multiplied by a transonic factor `1 + 0.8 exp(−((M−1.1)/0.35)²)` (minus a small supersonic reduction).
Drag acting behind the centre of mass gives weathercock stability; fins move the centre of pressure aft. (Limitation: no shielding between neighbouring radial parts, no aerodynamic heating — roadmap.)

## Contact, landing and crashes
* Terrain height is `TerrainModel.SurfaceHeight(direction)`, identical to what is rendered; oceans are a firm surface at sea level (no buoyancy: documented limitation).
* Each part has sample points (end-face centres and rims; boxes use corners; deployed legs add a foot point). Only the lowest 28 are tested against the terrain each step.
* **Sequential impulses** (6 iterations) with Coulomb friction (hull 0.8, legs 1.1), restitution 0.05 above 1 m/s, positional correction with 1 cm slop, surface velocity `ω × r` included so the pad moves with the planet.
* **Crash**: the approach speed at a contact point (before impulses) above the part's `crashTolerance` (m/s; legs 14, water ×2.5) destroys that part; children become debris.
* **Rest**: speed < 0.1 m/s, spin < 0.03 rad/s and no thrust for 1 s → the craft locks to the surface (zero simulation cost; warp allowed).

## Structural failure
For every joint the load needed to give the sub-tree beyond it its observed acceleration is computed bottom-up:
`F = Σ(mᵢ aᵢ − Fᵢ,ext)`, `M = Σ(xᵢ − xⱼ)×mᵢaᵢ − Σ(pᵢ − xⱼ)×Fᵢ,ext`, with `aᵢ = a_cm + α×rᵢ + ω×(ω×rᵢ)` (gravity cancels). A joint fails when its `jointStrength` (N) or `jointTorque` (N·m) is exceeded for 0.1 s, or at once beyond 3×. External forces include thrust, aerodynamic loads, RCS and contact reactions. A vehicle that has not left the pad and moves slower than 4 m/s is treated as held by launch clamps (contact reactions are not analysed). A failed joint calls `Craft.Detach`.

## Control
* **SAS** (`ControlSystem`): attitude error is the rotation vector between current and target orientation (shortest arc). Per axis: `ω_des = clamp(min(k·θ, √(2·0.6·α_max·θ)), ±1.2 rad/s)`, `α = clamp((ω_des − ω)/0.25 s, ±α_max)`, torque command `I·α`. Targets: held attitude, prograde/retrograde, normal/anti-normal, radial in/out (surface or orbital reference automatically: surface below half the atmosphere height, orbit above), manoeuvre-node burn direction.
* The normalised command vector `c` (each axis −1…1) is shared by all actuators; each contributes `c × its authority` (reaction wheels use electric charge, RCS nozzles fire when `τᵢ·ĉ > 0.2`, gimbals deflect, fins deflect), so their sum is the requested torque.
* A craft needs a command module with power (probes draw electric charge) to be controlled.

## Power
Solar panels: `charge/s = rate × (flux/flux_ref) × cos(angle)` (tracking panels ignore the cosine), zero when deployed = false or in the shadow of the SoI body or its parent (ray–sphere test), with `flux ∝ 1/d²` from the star. Probe cores and reaction wheels draw electric charge; batteries are tanks of `ElectricCharge`.

## Verification summary
See `tests/OSP.Core.Tests`: gravity, escape/circular speed, period, Ap/Pe/e/i, Kepler propagation vs elements, energy conservation at all warp levels, SoI transitions, patched-conic Lumen encounter, fuel consumption (`mdot = F/(Isp g₀)`), Δv vs Tsiolkovsky (0.2 %), thrust direction, staging order, momentum-conserving separation, parachute terminal velocity `√(2mg/(ρ C_d A))` (12 %), aerodynamic restoring torque, landing/crash, structural failure, save/load round trips and corrupted data, and a complete scripted orbital insertion.
