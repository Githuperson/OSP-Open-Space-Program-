// Patched-conic trajectory prediction: propagate a vessel state forward,
// following sphere-of-influence transitions, detecting impacts and escapes,
// and applying maneuver nodes along the way. Used by the map view, the 3D
// trajectory line and the maneuver planner.

import * as THREE from 'three';
import { bodyPos, bodyVel, BODY_BY_ID, STAR, primaryFor } from './universe.js';
import { rvToOE, keplerProp, componentsToDv } from './conics.js';
import { TAU } from './util.js';

// Propagate a predicted trajectory.
//   start: { r, v, ut } in the SYSTEM frame
//   opts: {
//     primary:    body id to start in (default: star)
//     maxDepth:   max SOI transitions (default 8)
//     maxTime:    max seconds per segment (default 1.5 local periods)
//     maxPoints:  total sample budget (default 4000)
//     nodes:      maneuver nodes to apply [{ut, dvp, dvr, dvn, applied?}]
//     stopAfterNode: stop the preview after applying this node index
//   }
// Returns {
//   segments: [{ body, points: [{t, r(system)}] }]
//   impact:   {body, t, r} | null
//   escape:   bool
//   endState: {r, v, ut, primary}
//   appliedThrough: last node index applied (-1 if none)
// }
export function predictTrajectory(start, opts = {}) {
  const maxDepth = opts.maxDepth ?? 8;
  const maxPoints = opts.maxPoints ?? 4000;
  const nodes = (opts.nodes || [])
    .filter((n) => !n.applied)
    .slice()
    .sort((a, b) => a.ut - b.ut);
  const stopAfterNode = opts.stopAfterNode ?? Infinity;

  const segments = [];
  let impact = null;
  let escape = false;

  let primary = opts.primary ? BODY_BY_ID[opts.primary] : STAR;
  let rSys = start.r.clone();
  let vSys = start.v.clone();
  let ut = start.ut;
  let nodeIdx = 0;
  let appliedThrough = -1;
  let totalPts = 0;

  for (let depth = 0; depth < maxDepth; depth++) {
    if (impact || escape || totalPts >= maxPoints) break;

    // relative state in the current primary's frame
    let rel = rSys.clone().sub(bodyPos(primary, ut));
    let relv = vSys.clone().sub(bodyVel(primary, ut));
    const oe0 = rvToOE(rel, relv, primary.mu);

    let segDur = oe0.period !== Infinity ? oe0.period * 1.5 : 30 * 86400;
    // escape trajectories need time to actually leave the SOI
    if (oe0.energy >= 0) segDur = Math.max(segDur, primary.parent ? 60 * 86400 : 200 * 86400);
    // extend the segment so the next maneuver node is covered by the preview
    if (nodeIdx < nodes.length) {
      segDur = Math.max(segDur, (nodes[nodeIdx].ut - ut) * 1.02 + 60);
    }
    segDur = Math.min(segDur, 400 * 86400);
    if (opts.maxTime) segDur = Math.min(segDur, opts.maxTime);
    const budget = Math.max(32, Math.min(1500, maxPoints - totalPts));

    const points = [{ t: ut, r: rSys.clone() }];
    let t = 0;
    let dt = segDur / budget;
    let exitBody = null;
    let nodeHit = null;
    let lastSysV = vSys.clone();
    let lastUt = ut;

    // Adaptive time stepping: fine near the planet (impact/SOI safety),
    // growing geometrically with distance so escapes stay in budget.
    for (let i = 0; i < budget && t < segDur; i++) {
      const rn = Math.max(rel.length(), 1);
      const tOrb = TAU * Math.sqrt((rn ** 3) / primary.mu);
      dt = Math.min(dt, tOrb / 120, segDur - t);
      if (dt <= 1e-9) break;
      const p = keplerProp(rel, relv, dt, primary.mu);
      rel = p.r;
      relv = p.v;
      t += dt;
      const utNow = ut + t;
      const sysR = rel.clone().add(bodyPos(primary, utNow));
      const sysV = relv.clone().add(bodyVel(primary, utNow));
      points.push({ t: utNow, r: sysR });
      lastSysV = sysV;
      lastUt = utNow;

      // a maneuver node is due: end this segment here
      if (nodeIdx < nodes.length && nodes[nodeIdx].ut <= utNow) {
        nodeHit = { node: nodes[nodeIdx], sysR: sysR.clone(), sysV: sysV.clone(), utNow, rel: rel.clone(), relv: relv.clone() };
        break;
      }

      // impact with the surface
      if (rel.length() < primary.radius) {
        impact = { body: primary.id, t: utNow, r: sysR.clone() };
        break;
      }

      // sphere-of-influence transition
      const newPrimary = primaryFor(primary.id, sysR, utNow);
      if (newPrimary.id !== primary.id) { exitBody = newPrimary; break; }

      // escape from the system entirely
      const energy = (relv.lengthSq() / 2) - primary.mu / rel.length();
      if (primary.id === STAR.id && energy >= 0 && sysR.length() > 4e11) {
        escape = true;
        break;
      }

      dt = Math.min(dt * 1.05, segDur / 200);
    }

    segments.push({ body: primary.id, points });
    totalPts += points.length;

    if (impact || escape) break;

    if (nodeHit) {
      // apply the node's delta-v and continue from the node point
      const { node, sysR, sysV, utNow, rel, relv } = nodeHit;
      const dv = componentsToDv(
        { prograde: node.dvp, radial: node.dvr, normal: node.dvn }, rel, relv
      );
      node.predictedPos = sysR.clone();
      node.predictedDv = dv.clone();
      appliedThrough = nodeIdx;
      nodeIdx++;
      rSys = sysR;
      vSys = sysV.clone().add(dv);
      ut = utNow;
      if (appliedThrough >= stopAfterNode) break;
      continue;
    }

    if (!exitBody) break; // ran out of segment time budget

    primary = exitBody;
    rSys = points[points.length - 1].r.clone();
    vSys = lastSysV.clone();
    ut = lastUt;
  }

  return {
    segments,
    impact,
    escape,
    endState: { r: rSys, v: vSys, ut, primary: primary.id },
    appliedThrough,
  };
}

// Flatten segments to a polyline of system-frame Vector3.
export function trajectoryPoints(pred, maxPts = 4000) {
  const pts = [];
  for (const seg of pred.segments) {
    for (const p of seg.points) {
      pts.push(p.r);
      if (pts.length >= maxPts) return pts;
    }
  }
  return pts;
}

// Find the closest approach between a predicted trajectory and a target
// body's position over time. Returns {d, t, r, targetR} or null.
export function closestApproach(pred, targetBody, tStart) {
  let best = null;
  for (const seg of pred.segments) {
    for (const p of seg.points) {
      if (p.t < tStart) continue;
      const tp = bodyPos(targetBody, p.t);
      const d = p.r.distanceTo(tp);
      if (!best || d < best.d) best = { d, t: p.t, r: p.r.clone(), targetR: tp.clone() };
    }
  }
  return best;
}
