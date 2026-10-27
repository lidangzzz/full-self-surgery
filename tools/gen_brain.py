#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Generates a life-size, anatomically-shaped brain as an explicit-hexahedron
VTK mesh for SOFA FEM simulation.

Structure (from a structured nx x ny x nz lattice warped into brain form):
  - two hemispheres split by the interhemispheric (medial) fissure
  - frontal taper, occipital fullness, flattened temporal base
  - gyri/sulci folds: multi-frequency radial modulation on the surface
  - cerebellum: bilateral posterior-inferior bulges
  - brainstem: posterior-medial stalk descending below the base
"""

import math

NX, NY, NZ = 22, 16, 15           # lattice resolution (5280 nodes)
L, H, W = 0.165, 0.105, 0.140     # life-size cerebrum length/height/width (m)
CY = 0.538 + H * 0.5              # centre height above the table (m)
CZ = 0.90                         # table centre (m)


def clamp01(t):
    return 0.0 if t < 0 else (1.0 if t > 1 else t)


def smooth01(t):
    t = clamp01(t)
    return t * t * (3 - 2 * t)


def brain_point(u, v, w):
    """Maps lattice coords (u,v,w in [0,1]) to a brain point (SOFA frame)."""
    sy = 2 * v - 1
    rad = max(0.0, 1.0 - sy * sy * 0.96)
    x = L * (u - 0.5) * math.sqrt(rad)
    z = W * (w - 0.5) * math.sqrt(rad)
    y = CY + H * 0.5 * sy * (0.78 + 0.22 * math.sqrt(rad))

    # Interhemispheric fissure: split |z| < gap, deeper toward the top.
    gap = 0.004 + 0.006 * smooth01((v - 0.55) / 0.4)
    az = abs(z)
    if az < W * 0.5:
        s = smooth01((az - gap) / (W * 0.42 - gap + 1e-9))
        z = math.copysign(gap + (az - gap) * (0.75 + 0.25 * s), z)  # gentler fissure (element quality)

    # Frontal lobe (x>0) narrower and slightly lower; occipital fuller.
    if x > 0:
        t = smooth01(x / (L * 0.5))
        z *= 1.0 - 0.30 * t
        y -= H * 0.06 * t
    else:
        z *= 1.0 - 0.06 * smooth01(-x / (L * 0.5))

    # Flattened temporal base.
    if v < 0.28:
        y = CY - H * 0.5 + (y - (CY - H * 0.5)) * (0.55 + 1.625 * v / 0.28)

    # Gyri/sulci folds on the surface.
    theta = math.atan2(z, x + 1e-9)
    surfaceness = smooth01((abs(sy) * 0.5 + math.sqrt(x * x + z * z) / (0.5 * max(L, W)) * 0.9) - 0.55)
    fold = (0.0022 * math.sin(9 * theta + 2.0 * sy)
            + 0.0018 * math.sin(14 * theta + 5.0 * sy)
            + 0.0014 * math.sin(6 * theta + 3.0 * u))
    r_xz = math.hypot(x, z)
    if r_xz > 1e-6:
        k = 1.0 + fold * surfaceness / r_xz
        x *= k
        z *= k
    y += fold * surfaceness * 0.8

    # Cerebellum: bilateral posterior-inferior bulges.
    cereb = smooth01((-x - L * 0.18) / (L * 0.22)) * smooth01((0.34 - v) / 0.22)
    if cereb > 0:
        az2 = abs(z)
        bulge = cereb * 0.85 * smooth01((az2 - W * 0.10) / (W * 0.14))
        y -= H * 0.22 * bulge
        z *= 1.0 + 0.35 * bulge
        if bulge > 0.35:
            y += 0.0022 * math.sin(2200 * az2) * cereb * surfaceness

    # Brainstem: posterior-medial stalk below the base.
    stem = smooth01(1.0 - math.hypot(x + L * 0.16, z) / 0.020)
    if stem > 0 and v < 0.22:
        pull = stem * smooth01((0.22 - v) / 0.22)
        y -= H * 0.30 * pull
        z *= 1.0 - 0.25 * pull
        x += L * 0.06 * pull

    return x, y, z + CZ  # table-centre offset


def main():
    pts = []
    for k in range(NZ):
        for j in range(NY):
            for i in range(NX):
                pts.append(brain_point(i / (NX - 1), j / (NY - 1), k / (NZ - 1)))

    def idx(i, j, k):
        return i + j * NX + k * NX * NY

    cells = []
    for k in range(NZ - 1):
        for j in range(NY - 1):
            for i in range(NX - 1):
                cells.append((idx(i, j, k), idx(i, j, k + 1), idx(i + 1, j, k + 1), idx(i + 1, j, k),
                              idx(i, j + 1, k), idx(i, j + 1, k + 1), idx(i + 1, j + 1, k + 1), idx(i + 1, j + 1, k)))

    ys = [p[1] for p in pts]
    out = ["# vtk DataFile Version 3.0",
           "SofaRobotAI brain - life-size structured hexahedra",
           "ASCII", "DATASET UNSTRUCTURED_GRID",
           f"POINTS {len(pts)} double"]
    out += [f"{p[0]:.6f} {p[1]:.6f} {p[2]:.6f}" for p in pts]
    out.append(f"CELLS {len(cells)} {len(cells) * 9}")
    out += ["8 " + " ".join(str(c) for c in cell) for cell in cells]
    out.append(f"CELL_TYPES {len(cells)}")
    out += ["12"] * len(cells)

    path = "Assets/StreamingAssets/SOFA/brain.vtk"
    with open(path, "w") as f:
        f.write("\n".join(out) + "\n")
    print(f"brain.vtk: {len(pts)} nodes, {len(cells)} hexahedra")
    print(f"dims nx={NX} ny={NY} nz={NZ} bounds y=[{min(ys):.3f}..{max(ys):.3f}]")


if __name__ == "__main__":
    main()
