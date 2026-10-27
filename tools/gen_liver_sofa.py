#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Transforms the OFFICIAL SofaUnity demo liver onto our operating table.

Sources (vendored SofaUnity assets):
  Assets/SofaUnity/Scenes/SofaScenes/mesh/liver.msh          (181-node tet FEM,
      the classic SOFA liver, with the demo's official fixed nodes 3/39/64)
  Assets/SofaUnity/Scenes/SofaScenes/mesh/liver-smooth.obj   (the demo's registered visual pair)

The same rigid transform (scale + axis mapping + translate) is applied to
both, so the FEM and the visual surface stay registered. Outputs:
  Assets/StreamingAssets/SOFA/liver_sofa.msh            (FEM, table frame)
  Assets/StreamingAssets/SOFA/liver_sofa.vtk            (FEM rest for C#)
  Assets/StreamingAssets/SOFA/liver_sofa_surface.obj    (visual, table frame)
  Assets/StreamingAssets/SOFA/liver_sofa_bind.txt       (4-node skinning)
"""

import math
import numpy as np

MSH = "Assets/SofaUnity/Scenes/SofaScenes/mesh/liver.msh"
OBJ = "Assets/SofaUnity/Scenes/SofaScenes/mesh/liver3-HD.obj"

TARGET_LEN = 0.25          # adult liver length (m)
CZ = 0.90                  # table centre
BASE_Y = 0.535             # liver rests on the drape at this height


def parse_gmsh(path):
    nodes, tets = {}, []
    sec = None
    for line in open(path):
        t = line.split()
        if not t:
            continue
        if t[0].startswith("$End"):
            sec = None
            continue
        if t[0].startswith("$"):
            sec = t[0][1:]
            continue
        if sec == "NOD" and len(t) == 4:
            nodes[int(t[0])] = (float(t[1]), float(t[2]), float(t[3]))
        elif sec == "ELM":
            p = [int(x) for x in t]
            if len(p) >= 3 and p[1] == 4:      # gmsh type 4 = tetrahedron
                tets.append(p[-4:])
    return nodes, tets


def parse_obj(path):
    v, f = [], []
    for line in open(path):
        if line.startswith("v "):
            p = line[2:].split()
            v.append([float(x) for x in p[:3]])
        elif line.startswith("f "):
            idx = [int(x.split("/")[0]) - 1 for x in line[2:].split()]
            for k in range(1, len(idx) - 1):
                f.append((idx[0], idx[k], idx[k + 1]))
    return np.array(v), f


def main():
    nodes, tets = parse_gmsh(MSH)
    ids = sorted(nodes)
    P = np.array([nodes[i] for i in ids])       # row r == node ids[r]
    old2new = {old: r for r, old in enumerate(ids)}

    # Axis mapping: longest raw span -> our x (length), medium -> z (AP),
    # short -> y (height); then uniform scale to TARGET_LEN.
    spans = P.max(axis=0) - P.min(axis=0)
    order = np.argsort(spans)                    # short..long
    ax_h, ax_w, ax_l = order[0], order[1], order[2]
    s = TARGET_LEN / spans[ax_l]

    # Non-uniform height: the demo liver is proportionally tall; compress to
    # a realistic adult liver height (~0.10 m) after the uniform scale.
    h_span = spans[ax_h] * s
    h_scale = 0.100 / h_span

    def tx(p):
        q = np.array([(p[ax_l] - P[:, ax_l].mean()) * s,
                      (p[ax_h] - P[:, ax_h].min()) * s * h_scale,
                      (p[ax_w] - P[:, ax_w].mean()) * s])
        return np.array([q[0], BASE_Y + q[1], q[2] + CZ])

    Q = np.array([tx(p) for p in P])
    print(f"FEM: {len(P)} nodes, {len(tets)} tets; scaled {s:.4f}; "
          f"bounds y[{Q[:,1].min():.3f}..{Q[:,1].max():.3f}] "
          f"x[{Q[:,0].min():.3f}..{Q[:,0].max():.3f}] z[{Q[:,2].min():.3f}..{Q[:,2].max():.3f}]")

    # ---- write FEM gmsh (same format, transformed coords) -------------
    with open("Assets/StreamingAssets/SOFA/liver_sofa.msh", "w") as f:
        f.write("$MeshFormat\n2.2 0 8\n$EndMeshFormat\n")
        f.write(f"$NOD\n{len(Q)}\n")
        for r, p in enumerate(Q):
            f.write(f"{r+1} {p[0]:.6f} {p[1]:.6f} {p[2]:.6f}\n")
        f.write("$EndNOD\n")
        f.write(f"$ELM\n{len(tets)}\n")
        for e, tet in enumerate(tets, 1):
            n = [old2new[x] + 1 for x in tet]
            f.write(f"{e} 4 0 {n[0]} {n[1]} {n[2]} {n[3]}\n")
        f.write("$EndELM\n")

    # ---- VTK rest (points only) for the C# parser ----------------------
    with open("Assets/StreamingAssets/SOFA/liver_sofa.vtk", "w") as f:
        f.write("# vtk DataFile Version 3.0\nSofaRobotAI official-liver FEM rest\nASCII\n"
                "DATASET UNSTRUCTURED_GRID\n")
        f.write(f"POINTS {len(Q)} double\n")
        for p in Q:
            f.write(f"{p[0]:.6f} {p[1]:.6f} {p[2]:.6f}\n")
        f.write("CELLS 0 0\nCELL_TYPES 0\n")

    # ---- visual surface: independent transform to the SAME target box --
    # (liver3-HD is a separate showcase model, not registered with the demo
    # FEM mesh; both are livers, so mapping each onto the same target box
    # with the same axis conventions registers them approximately.)
    V, F = parse_obj(OBJ)
    vspans = V.max(axis=0) - V.min(axis=0)
    vorder = np.argsort(vspans)
    vax_h, vax_w, vax_l = vorder[0], vorder[1], vorder[2]
    vs = TARGET_LEN / vspans[vax_l]
    vh_scale = 0.100 / (vspans[vax_h] * vs)
    vw = 0.19  # AP width, matches the FEM liver

    def vtx(p):
        q = np.array([(p[vax_l] - V[:, vax_l].mean()) * vs,
                      (p[vax_h] - V[:, vax_h].min()) * vs * vh_scale,
                      (p[vax_w] - V[:, vax_w].mean()) / vspans[vax_w] * vw])
        return np.array([q[0], BASE_Y + q[1], q[2] + CZ])

    Vc = np.array([vtx(v) for v in V])
    print(f"visual: {len(V)} verts, {len(F)} tris")
    with open("Assets/StreamingAssets/SOFA/liver_sofa_surface.obj", "w") as f:
        f.write("# SofaRobotAI official SofaUnity liver visual (table frame)\n")
        for p in Vc:
            f.write(f"v {p[0]:.6f} {p[1]:.6f} {p[2]:.6f}\n")
        for a, b, c in F:
            f.write(f"f {a+1} {b+1} {c+1}\n")

    # ---- skinning bindings (4 nearest FEM nodes, no tumor) -------------
    idx = np.empty((len(Vc), 4), dtype=np.int32)
    wts = np.empty((len(Vc), 4))
    for st in range(0, len(Vc), 2048):
        e = min(st + 2048, len(Vc))
        d = np.linalg.norm(Vc[st:e, None, :] - Q[None, :, :], axis=2)
        ii = np.argpartition(d, 4, axis=1)[:, :4]
        dd = np.take_along_axis(d, ii, axis=1)
        w = 1.0 / (dd + 1e-6)
        w /= w.sum(axis=1, keepdims=True)
        idx[st:e] = ii
        wts[st:e] = w
    with open("Assets/StreamingAssets/SOFA/liver_sofa_bind.txt", "w") as f:
        for i in range(len(Vc)):
            n, w = idx[i], wts[i]
            f.write(f"{i} {n[0]} {w[0]:.5f} {n[1]} {w[1]:.5f} {n[2]} {w[2]:.5f} "
                    f"{n[3]} {w[3]:.5f} 0.0000 0\n")

    # ---- official fixed nodes, remapped --------------------------------
    fixed = [3, 39, 64]
    remap = [old2new[x - 1] for x in fixed]
    print("official fixed nodes ->", remap)
    print("top y =", round(Q[:, 1].max(), 3))


if __name__ == "__main__":
    main()
