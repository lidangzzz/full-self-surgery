#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Life-size anatomical liver as an explicit-hexahedron VTK lattice for SOFA.

Shape: large domed right lobe + tapered left lobe, smooth diaphragmatic
(superior) dome, flatter visceral (inferior) surface with a porta-hepatis
indentation posteriorly, falciform notch between the lobes anteriorly.
The structured lattice stays connected (single hexa block) for the Unity
surface-topology builder.
"""

import math

NX, NY, NZ = 18, 10, 13          # lattice resolution (2340 nodes)
L, H, W = 0.125, 0.045, 0.080    # half-extents: length/height/AP-width (m)
CY = 0.535 + H                   # centre height above the table
CZ = 0.90                        # table centre


def clamp01(t):
    return 0.0 if t < 0 else (1.0 if t > 1 else t)


def smooth01(t):
    t = clamp01(t)
    return t * t * (3 - 2 * t)


def liver_point(u, v, w):
    """(u,v,w in [0,1]) -> liver point (SOFA frame)."""
    # Ellipsoid radial base.
    sy = 2 * v - 1
    rad = max(0.0, 1.0 - sy * sy * 0.92)
    x = L * (2 * u - 1) * math.sqrt(rad)
    z = W * (2 * w - 1) * math.sqrt(rad)
    y = CY + H * sy * (0.85 + 0.15 * math.sqrt(rad))

    # Right lobe (x<0): bigger & taller; left lobe (x>0): tapered, thinner.
    right = smooth01(-x / L)
    left = smooth01(x / L)
    y += H * 0.18 * right          # right dome taller
    z *= 1.0 + 0.10 * right
    z *= 1.0 - 0.42 * left * smooth01((x - L * 0.25) / (L * 0.75))
    y -= H * 0.28 * left * smooth01(x / L)

    # Diaphragmatic dome (top): gently convex; visceral (bottom): flatter.
    if v < 0.35:
        y = CY - H + (y - (CY - H)) * (0.85 + 0.43 * v / 0.35)

    # Porta hepatis: posterior-inferior indentation (vessel entry).
    porta = smooth01(1.0 - math.hypot(x + 0.02, z + W * 0.75) / 0.030)
    if porta > 0 and v < 0.45:
        pull = porta * smooth01((0.45 - v) / 0.45)
        y -= H * 0.18 * pull
        z += 0.006 * pull

    # Falciform notch: thin anterior groove between the lobes.
    if x > 0.004:
        notch = smooth01(1.0 - math.hypot(x - 0.012, z - W * 0.85) / 0.016)
        y -= H * 0.14 * notch * smooth01((0.5 - v) / 0.5)

    # Bare-area posterior flattening (where the liver is pinned).
    # bare-area 后部不再压缩（单元质量优先）

    return x, y, z + CZ


def main():
    pts = []
    for k in range(NZ):
        for j in range(NY):
            for i in range(NX):
                pts.append(liver_point(i / (NX - 1), j / (NY - 1), k / (NZ - 1)))

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
           "SofaRobotAI liver - life-size structured hexahedra",
           "ASCII", "DATASET UNSTRUCTURED_GRID",
           f"POINTS {len(pts)} double"]
    out += [f"{p[0]:.6f} {p[1]:.6f} {p[2]:.6f}" for p in pts]
    out.append(f"CELLS {len(cells)} {len(cells) * 9}")
    out += ["8 " + " ".join(str(c) for c in cell) for cell in cells]
    out.append(f"CELL_TYPES {len(cells)}")
    out += ["12"] * len(cells)

    with open("Assets/StreamingAssets/SOFA/liver.vtk", "w") as f:
        f.write("\n".join(out) + "\n")
    print(f"liver.vtk: {len(pts)} nodes, {len(cells)} hexahedra, dims nx={NX} ny={NY} nz={NZ} y[{min(ys):.3f}..{max(ys):.3f}]")


if __name__ == "__main__":
    main()
