#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
High-resolution anatomical brain render surface + skinning bindings to the
coarse SOFA FEM lattice (brain.vtk).

Parts (all life-size, SOFA world frame, centred at z=0.90 on the table):
  - two cerebral hemispheres with flat medial walls (interhemispheric fissure),
    regional lobe shaping (frontal taper, temporal bulge, occipital fullness),
    multi-octave gyri/sulci, a deep Sylvian fissure and a central sulcus
  - cerebellum: bilateral lobes + vermis with fine transverse folia
  - brainstem: tapered stalk with pons bulge

Every render vertex is skinned to the 4 nearest FEM nodes (inverse-distance
weights) so the fine surface follows the SOFA deformation exactly, while the
physics keeps its proven stable resolution. Fold depth per vertex is exported
for sulcal shading in Unity.

Outputs:
  Assets/StreamingAssets/SOFA/brain_surface.obj   (v/f)
  Assets/StreamingAssets/SOFA/brain_bind.txt      (vidx n1 w1 n2 w2 n3 w3 n4 w4 depth)
"""

import math
import numpy as np

# ---- keep in sync with gen_brain.py (physics lattice) ---------------------
NX, NY, NZ = 22, 16, 15
CZ = 0.90
CY = 0.538 + 0.105 * 0.5


def smooth01(t):
    t = np.clip(t, 0.0, 1.0)
    return t * t * (3 - 2 * t)


# ============================ cerebral hemisphere ============================
def hemisphere(side, nu=140, nv=100):
    """One hemisphere as a closed sphere mapped to a brain hemisphere:
    lateral surface = folded cortex, medial side = flattened wall.
    side=+1 -> z>0 (right), side=-1 -> z<0 (left)."""
    u = np.linspace(0, 1, nu)                # longitude
    v = np.linspace(0, 1, nv)                # latitude 0=bottom 1=top
    U, V = np.meshgrid(u, v, indexing="ij")
    theta = U * 2 * math.pi
    phi = V * math.pi

    D = np.stack([np.cos(theta) * np.sin(phi),
                  np.cos(phi),                       # -1 bottom .. +1 top
                  np.sin(theta) * np.sin(phi) * side], axis=-1)  # z by side

    # Regional radius shaping (metres) -----------------------------------
    L, Hh, W = 0.080, 0.041, 0.069          # per-hemisphere length/height/width
    lat = D[..., 2] * side                   # 1 lateral .. -1 medial
    dx, dy, dz = D[..., 0], D[..., 1], D[..., 2] * side
    frontal = smooth01(dx / 0.95)            # +x anterior
    posterior = smooth01(-dx / 0.95)
    inferior = smooth01(-dy / 0.9)

    rx = np.full_like(dx, L)
    ry = np.full_like(dy, Hh)
    rz = np.full_like(dz, W)

    # Frontal lobe narrower & slightly lower; occipital full.
    rz *= 1.0 - 0.22 * frontal
    ry *= 1.0 - 0.05 * frontal
    rz *= 1.0 + 0.05 * posterior
    # Temporal bulge: low, anterior-lateral.
    temporal = smooth01((dz - 0.15) / 0.6) * smooth01((0.2 - dy) / 0.55) * smooth01((dx + 0.1) / 0.7)
    ry *= 1.0 + 0.10 * temporal
    rz *= 1.0 + 0.30 * temporal
    # Flatten the base.
    base = smooth01((-dy - 0.35) / 0.5)
    ry *= 1.0 - 0.22 * base

    # Multi-octave gyri/sulci (ridge amplitude in metres, ~real 3-8mm) -----
    th_, ph_ = theta, phi
    fold = (0.0042 * np.sin(2.3 * th_ + 1.1) * np.sin(3.7 * ph_ + 0.7)
            + 0.0031 * np.sin(5.1 * th_ + 2.3) * np.sin(6.3 * ph_ + 1.9)
            + 0.0021 * np.sin(8.7 * th_ + 4.1) * np.sin(9.1 * ph_ + 3.3)
            + 0.0013 * np.sin(13.3 * th_ + 0.9) * np.sin(15.7 * ph_ + 2.6))
    # Gyri only on the lateral convexity, none on the medial wall/base.
    cortex_zone = smooth01((lat - 0.12) / 0.35) * (1.0 - 0.65 * base)
    fold *= cortex_zone

    # Sylvian fissure: deep diagonal trench on the lateral surface.
    s_t = smooth01((th_ - (0.55 + 0.35 * ph_)) / 0.16)
    sylvian = -0.0075 * s_t * smooth01((ph_ - 0.38) / 0.22) * smooth01((0.75 - ph_) / 0.18)
    # Central sulcus: one prominent upper-lateral groove.
    c_t = smooth01((th_ - (2.05 - 0.3 * ph_)) / 0.10)
    central = -0.0052 * c_t * smooth01((ph_ - 0.52) / 0.18)

    bump = fold + sylvian + central
    rx = rx * (1 + bump / L * 1.6)
    ry = ry * (1 + bump / Hh * 1.3)
    rz = rz * (1 + bump / W * 1.6)

    P = np.stack([dx * rx, dy * ry, dz * rz], axis=-1)
    P[..., 1] += CY
    P[..., 2] += CZ

    # Medial flattening: fold z<0 hemisphere-side onto a slightly bulged wall.
    wall_z = 0.0042 + 0.0016 * np.cos(dx * 8.0) * np.cos(dy * 6.0)
    medial = smooth01((-P[..., 2] * side + 0.0) / 0.012)
    medial = smooth01((-dz) / 0.30)
    z_med = side * (wall_z + (P[..., 2] - CZ) * 0.0) + CZ
    # Keep x/y profile of the wall from the original direction.
    P2 = P.copy()
    P2[..., 2] = np.where(dz < 0, z_med + (P[..., 2] - CZ) * 0.12, P[..., 2])
    P2[..., 0] = np.where(dz < 0, P[..., 0] * 0.55, P2[..., 0])
    P2[..., 1] = np.where(dz < 0, P[..., 1] - (P[..., 1] - CY) * 0.22, P2[..., 1])

    # depth field for sulcal shading: -bump normalised (grooves darker)
    depth = np.clip(-(sylvian + central + fold * 0.8) / 0.0075, 0, 1)
    depth *= cortex_zone
    return P2.reshape(-1, 3), depth.reshape(-1), nu, nv


def grid_faces(nu, nv, offset):
    f = []
    for i in range(nu - 1):
        for j in range(nv - 1):
            a = offset + i * nv + j
            b = offset + (i + 1) * nv + j
            c = offset + (i + 1) * nv + j + 1
            d = offset + i * nv + j + 1
            f += [(a, b, c), (a, c, d)]
    return f


# ============================ cerebellum + brainstem ========================
def cerebellum(side, nu=70, nv=44):
    """One cerebellar hemisphere with transverse folia + vermis blend."""
    u = np.linspace(0, 1, nu)
    v = np.linspace(0, 1, nv)
    U, V = np.meshgrid(u, v, indexing="ij")
    theta = U * 2 * math.pi
    phi = V * math.pi
    D = np.stack([np.cos(theta) * np.sin(phi), np.cos(phi), np.sin(theta) * np.sin(phi)], axis=-1)

    L, Hh, W = 0.052, 0.030, 0.034
    # folia: fine transverse ridges along AP (x)
    folia = 0.0012 * np.sin(2 * math.pi * (U * nu) * 0.9 + 3.0 * V * nv * 0.2)
    # vermis side (medial, z*side<0) blends smaller
    lat = D[..., 2] * side
    verm = smooth01((-lat) / 0.35)
    P = np.stack([D[..., 0] * (L + folia * 4),
                  D[..., 1] * (Hh + folia * 2.4),
                  D[..., 2] * (W + folia * 2.8) * (1 - 0.25 * verm)], axis=-1)
    P[..., 0] -= 0.052          # posterior
    P[..., 1] += 0.545          # inferior-posterior
    P[..., 2] = P[..., 2] * side + side * 0.020 + CZ
    depth = np.clip(np.abs(folia) / 0.0012, 0, 1) * 0.5
    return P.reshape(-1, 3), depth.reshape(-1), nu, nv


def brainstem(nu=48, nv=20):
    u = np.linspace(0, 1, nu)
    v = np.linspace(0, 1, nv)
    U, V = np.meshgrid(u, v, indexing="ij")
    theta = U * 2 * math.pi
    t = V
    r = 0.0125 * (1.0 - 0.30 * t) + 0.004 * np.exp(-((t - 0.45) / 0.22) ** 2)  # pons bulge
    y = 0.552 - 0.052 * t
    P = np.stack([np.cos(theta) * r - 0.026,
                  y,
                  np.sin(theta) * r + CZ], axis=-1)
    depth = np.zeros(P[..., 0].shape)
    return P.reshape(-1, 3), depth.reshape(-1), nu, nv


# ============================ FEM lattice nodes =============================
def fem_nodes():
    """Reproduces gen_brain.py's warped lattice rest positions (SOFA frame)."""
    import importlib.util
    spec = importlib.util.spec_from_file_location("genbrain", "tools/gen_brain.py")
    gb = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(gb)
    pts = []
    for k in range(gb.NZ):
        for j in range(gb.NY):
            for i in range(gb.NX):
                pts.append(gb.brain_point(i / (gb.NX - 1), j / (gb.NY - 1), k / (gb.NZ - 1)))
    return np.array(pts)


def bindings_and_depth(verts, depths, fem):
    """4-nearest inverse-distance bindings (chunked for memory)."""
    out = np.empty((len(verts), 4), dtype=np.int32)
    wts = np.empty((len(verts), 4), dtype=np.float64)
    chunk = 2048
    for s in range(0, len(verts), chunk):
        e = min(s + chunk, len(verts))
        d = np.linalg.norm(verts[s:e, None, :] - fem[None, :, :], axis=2)  # (c, N)
        idx = np.argpartition(d, 4, axis=1)[:, :4]
        dd = np.take_along_axis(d, idx, axis=1)
        w = 1.0 / (dd + 1e-6)
        w /= w.sum(axis=1, keepdims=True)
        out[s:e] = idx
        wts[s:e] = w
    return out, wts


def main():
    parts = []
    all_v, all_d = [], []
    faces = []
    offset = 0

    for side in (1, -1):
        v, d, nu, nv = hemisphere(side)
        all_v.append(v); all_d.append(d)
        faces += grid_faces(nu, nv, offset)
        offset += len(v)
    for side in (1, -1):
        v, d, nu, nv = cerebellum(side)
        all_v.append(v); all_d.append(d)
        faces += grid_faces(nu, nv, offset)
        offset += len(v)
    v, d, nu, nv = brainstem()
    all_v.append(v); all_d.append(d)
    faces += grid_faces(nu, nv, offset)
    offset += len(v)

    V = np.vstack(all_v)
    Dp = np.concatenate(all_d)
    print(f"surface: {len(V)} verts, {len(faces)} tris")

    fem = fem_nodes()
    print(f"fem lattice: {len(fem)} nodes")
    idx, wts = bindings_and_depth(V, Dp, fem)

    with open("Assets/StreamingAssets/SOFA/brain_surface.obj", "w") as f:
        f.write("# SofaRobotAI high-res brain render surface (SOFA frame)\n")
        for p in V:
            f.write(f"v {p[0]:.6f} {p[1]:.6f} {p[2]:.6f}\n")
        for a, b, c in faces:
            f.write(f"f {a+1} {b+1} {c+1}\n")

    with open("Assets/StreamingAssets/SOFA/brain_bind.txt", "w") as f:
        for i in range(len(V)):
            n = idx[i]; w = wts[i]
            f.write(f"{i} {n[0]} {w[0]:.5f} {n[1]} {w[1]:.5f} {n[2]} {w[2]:.5f} {n[3]} {w[3]:.5f} {Dp[i]:.4f}\n")
    print("wrote brain_surface.obj + brain_bind.txt")


if __name__ == "__main__":
    main()
