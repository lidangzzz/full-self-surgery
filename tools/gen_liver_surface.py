#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""High-resolution anatomical liver surface + hepatic tumor, skinned to the
SOFA FEM lattice (liver.vtk).

Surface (life-size, smooth organ unlike the folded brain):
  - domed diaphragmatic surface, flatter visceral surface
  - large right lobe, tapered left lobe, falciform notch between them
  - porta hepatis indentation posteriorly
  - subtle surface vascular blush variation (vertex colour depth field)

TUMOR: lobulated hemispherical growth (HCC-like) on the RIGHT-lobe superior
surface, diameter ~36mm, slightly overhanging the capsule, flagged per-vertex
(column 11 in the bind file) so Unity shades it as tumor tissue.

Outputs:
  Assets/StreamingAssets/SOFA/liver_surface.obj
  Assets/StreamingAssets/SOFA/liver_bind.txt (vidx n1 w1 n2 w2 n3 w3 n4 w4 depth tumor)
"""

import math
import numpy as np

NX, NY, NZ = 18, 10, 13
CZ = 0.90
CY = 0.535 + 0.045

TUMOR_C = np.array([-0.052, CY + 0.040, 0.878])
TUMOR_R = 0.018


def smooth01(t):
    t = np.clip(t, 0.0, 1.0)
    return t * t * (3 - 2 * t)


def liver_surface(nu=160, nv=110):
    u = np.linspace(0, 1, nu)
    v = np.linspace(0, 1, nv)
    U, V = np.meshgrid(u, v, indexing="ij")
    theta = U * 2 * math.pi
    phi = V * math.pi
    D = np.stack([np.cos(theta) * np.sin(phi), np.cos(phi), np.sin(theta) * np.sin(phi)], axis=-1)

    L, H, W = 0.125, 0.045, 0.080
    dx, dy, dz = D[..., 0], D[..., 1], D[..., 2]

    right = smooth01(-dx / 0.95)
    left = smooth01(dx / 0.95)
    rx = np.full_like(dx, L)
    ry = np.full_like(dy, H * (1.0 + 0.20 * right - 0.30 * left))
    rz = np.full_like(dz, W * (1.0 + 0.10 * right - 0.20 * left))

    # Left lobe AP taper (wedge-shaped flap).
    rz *= 1.0 - 0.45 * smooth01((dx - 0.25) / 0.6) * smooth01((-dz) / 1.2 + 0.5)

    # Visceral (bottom) flattening.
    inferior = smooth01(-dy / 0.9)
    ry *= 1.0 - 0.25 * inferior

    # Subtle smoothness variation (vascular blush, ~1mm).
    blush = 0.0008 * np.sin(5 * theta + 1.2) * np.sin(4 * phi + 0.6)
    rx = rx * (1 + blush / L)
    ry = ry * (1 + blush / H)
    rz = rz * (1 + blush / W)

    P = np.stack([dx * rx, dy * ry, dz * rz], axis=-1)
    P[..., 1] += CY
    P[..., 2] += CZ

    # Porta hepatis: posterior-inferior dent.
    porta = smooth01(1.0 - np.hypot(P[..., 0] + 0.02, P[..., 2] - CZ + W * 0.75) / 0.032)
    pinf = porta * smooth01(-(P[..., 1] - CY) / (H * 0.9) + 0.5)
    P[..., 1] -= 0.016 * pinf
    P[..., 2] += 0.012 * pinf

    # Falciform notch: anterior superior thin groove between lobes.
    notch = smooth01(1.0 - np.hypot(P[..., 0] - 0.012, P[..., 2] - CZ - W * 0.85) / 0.015)
    P[..., 1] -= 0.013 * notch * smooth01((P[..., 1] - CY) / (H * 0.8) + 0.6)

    depth = np.clip(np.abs(blush) / 0.0008, 0, 1) * 0.25 + np.clip(notch + porta, 0, 1) * 0.5
    return P, depth, nu, nv


def add_tumor(P, depth):
    """Appends a lobulated tumor cap mesh over the right-lobe surface."""
    nu, nv = 90, 60
    u = np.linspace(0, 1, nu)
    v = np.linspace(0, 1, nv)
    U, V = np.meshgrid(u, v, indexing="ij")
    theta = U * 2 * math.pi
    rr = np.sin(V * math.pi / 2)          # hemisphere cap
    lobes = (1.0
             + 0.14 * np.sin(3 * theta + 0.8)
             + 0.09 * np.sin(5 * theta + 2.1)
             + 0.06 * np.sin(8 * theta + 4.0))
    R = TUMOR_R * rr * lobes
    X = np.cos(theta) * R
    Z = np.sin(theta) * R * 0.9
    Y = np.sqrt(np.clip(R ** 2 - X ** 2 - Z ** 2, 0, None))
    P2 = np.stack([TUMOR_C[0] + X, TUMOR_C[1] + Y, TUMOR_C[2] + Z], axis=-1)
    d2 = np.full(P2[..., 0].shape, 0.15)   # tumor vertex depth (slight shading)
    return P2.reshape(-1, 3), d2.reshape(-1), nu, nv


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


def fem_nodes():
    import importlib.util
    spec = importlib.util.spec_from_file_location("genliver", "tools/gen_liver.py")
    gl = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(gl)
    pts = []
    for k in range(gl.NZ):
        for j in range(gl.NY):
            for i in range(gl.NX):
                pts.append(gl.liver_point(i / (gl.NX - 1), j / (gl.NY - 1), k / (gl.NZ - 1)))
    return np.array(pts)


def main():
    Pv, dv, nu, nv = liver_surface()
    faces = grid_faces(nu, nv, 0)
    Pt, dt, tnu, tnv = add_tumor(Pv, dv)
    faces += grid_faces(tnu, tnv, len(Pv))

    V = np.vstack([Pv.reshape(-1, 3), Pt])
    Dp = np.concatenate([dv.reshape(-1), dt])
    tumor = np.concatenate([np.zeros(len(Pv.reshape(-1, 3))), np.ones(len(Pt))])
    print(f"liver surface: {len(V)} verts ({len(Pt)} tumor), {len(faces)} tris")

    fem = fem_nodes()
    idx = np.empty((len(V), 4), dtype=np.int32)
    wts = np.empty((len(V), 4), dtype=np.float64)
    for s in range(0, len(V), 2048):
        e = min(s + 2048, len(V))
        d = np.linalg.norm(V[s:e, None, :] - fem[None, :, :], axis=2)
        ii = np.argpartition(d, 4, axis=1)[:, :4]
        dd = np.take_along_axis(d, ii, axis=1)
        w = 1.0 / (dd + 1e-6)
        w /= w.sum(axis=1, keepdims=True)
        idx[s:e] = ii
        wts[s:e] = w

    with open("Assets/StreamingAssets/SOFA/liver_surface.obj", "w") as f:
        f.write("# SofaRobotAI high-res liver + tumor render surface (SOFA frame)\n")
        for p in V:
            f.write(f"v {p[0]:.6f} {p[1]:.6f} {p[2]:.6f}\n")
        for a, b, c in faces:
            f.write(f"f {a+1} {b+1} {c+1}\n")

    with open("Assets/StreamingAssets/SOFA/liver_bind.txt", "w") as f:
        for i in range(len(V)):
            n, w = idx[i], wts[i]
            f.write(f"{i} {n[0]} {w[0]:.5f} {n[1]} {w[1]:.5f} {n[2]} {w[2]:.5f} {n[3]} {w[3]:.5f} {Dp[i]:.4f} {int(tumor[i])}\n")
    print("wrote liver_surface.obj + liver_bind.txt")


if __name__ == "__main__":
    main()
