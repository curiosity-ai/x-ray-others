#!/usr/bin/env python3
"""IGES files with entities no CAD writer emits for a modern kernel: parametric spline surfaces (114), ruled surfaces (118), free-standing
bounded planes (108) and parametric spline curves (112), written with the small fixed-format writer in iges_writer.py, and the values
OpenCASCADE's IGES reader gives for them (area and bounds of what it builds; pip install cadquery-ocp) in
tests/XRay.ThreeD.Tests/Corpus/iges-splines-golden.json.

    python3 -I tools/fixtures/make_iges_splines.py dataset/synthetic tests/XRay.ThreeD.Tests/Corpus/iges-splines-golden.json

Layout of 114 as OpenCASCADE reads it: after M, N, TU and TV, an (M+1) by (N+1) table (i outer, j inner) of 48 numbers: 16 coefficients each for X, Y
and Z, the s power running fastest (s = u - TU(i), t = v - TV(j)). OpenCASCADE reads only the first segment of a 112 curve of several, so the
curve fixture has one segment; its closed-form values are also checked in the tests.
"""
import json, os, sys
from math import comb
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from iges_writer import write_iges
from OCP.IGESControl import IGESControl_Reader
from OCP.GProp import GProp_GProps
from OCP.BRepGProp import BRepGProp
from OCP.Bnd import Bnd_Box
from OCP.BRepBndLib import BRepBndLib

root, golden_path = sys.argv[1], sys.argv[2]


def measure(path):
    reader = IGESControl_Reader()
    reader.ReadFile(path)
    reader.TransferRoots()
    shape = reader.OneShape()
    area = GProp_GProps(); BRepGProp.SurfaceProperties_s(shape, area)
    box = Bnd_Box(); BRepBndLib.AddOptimal_s(shape, box, False, False)
    lo, hi = box.CornerMin(), box.CornerMax()
    return {'area': round(area.Mass(), 3), 'min': [round(lo.X(), 3), round(lo.Y(), 3), round(lo.Z(), 3)], 'max': [round(hi.X(), 3), round(hi.Y(), 3), round(hi.Z(), 3)]}


def line(a, b):
    return {'type': 110, 'params': [*a, *b]}


def arc(zt, cx, cy, sx, sy, ex, ey):
    return {'type': 100, 'params': [zt, cx, cy, sx, sy, ex, ey]}


golden = {}

# 114: X = u, Y = v, Z = a cubic polynomial in u and v; 2 by 2 patches over uneven breakpoints
c = [[0.2, -0.3, 0.5, 0], [0.1, 0.4, 0, 0], [0, -0.2, 0.0, 0.05], [0.03, 0, 0, 0]]
TU = [0.0, 1.0, 2.5]
TV = [0.0, 0.5, 2.0]


def local(coeffs, u0, v0):
    out = [[0] * 4 for _ in range(4)]
    for a in range(4):
        for b in range(4):
            for k in range(a + 1):
                for l in range(b + 1):
                    out[k][l] += coeffs[a][b] * comb(a, k) * u0 ** (a - k) * comb(b, l) * v0 ** (b - l)
    return out


def block(L):
    arr = [0] * 16
    for k in range(4):
        for l in range(4):
            arr[l * 4 + k] = L[k][l]
    return arr


M = N = 2
params = [3, 1, M, N] + TU + TV
X = [[0] * 4 for _ in range(4)]; X[1][0] = 1
Y = [[0] * 4 for _ in range(4)]; Y[0][1] = 1
for i in range(M + 1):
    for j in range(N + 1):
        for F in (X, Y, c):
            params += block(local(F, TU[min(i, M)], TV[min(j, N)]))
write_iges(f'{root}/iges-spline-surface.igs', [{'type': 114, 'params': params}])
golden['synthetic/iges-spline-surface.igs'] = measure(f'{root}/iges-spline-surface.igs')

# 118: line to line, quarter arc to line, and line to line reversed (a twisted strip)
# DE numbers are 2k+1: entity k (0-based) has DE 2k+1
ents = [
    line((0, 0, 0), (4, 0, 0)),            # DE 1
    line((0, 3, 1), (4, 3, 2)),            # DE 3
    {'type': 118, 'params': [1, 3, 0, 0]}, # DE 5
    arc(5, 10, 0, 12, 0, 10, 2),           # DE 7: quarter circle about (10, 0) at z = 5
    line((14, 0, 0), (14, 2, 0)),          # DE 9
    {'type': 118, 'params': [7, 9, 0, 0]}, # DE 11
    line((20, 0, 0), (24, 0, 0)),          # DE 13
    line((20, 3, 0), (24, 3, 0)),          # DE 15
    {'type': 118, 'params': [13, 15, 1, 0]},  # DE 17: reversed, so the strip twists into a bow tie
]
write_iges(f'{root}/iges-ruled.igs', ents)
golden['synthetic/iges-ruled.igs'] = measure(f'{root}/iges-ruled.igs')

# 108: a plane z = 2 bounded by an L-shaped composite curve of six lines (area 4 x 2 + 2 x 2 = 12)
pts = [(0, 0), (4, 0), (4, 2), (2, 2), (2, 4), (0, 4)]
ents = [line((*pts[i], 2), (*pts[(i + 1) % 6], 2)) for i in range(6)]               # DE 1, 3, ..., 11
ents.append({'type': 102, 'params': [6] + [1, 3, 5, 7, 9, 11]})                      # DE 13
ents.append({'type': 108, 'form': 1, 'params': [0, 0, 1, 2, 13, 0, 0, 0, 1]})        # DE 15
write_iges(f'{root}/iges-bounded-plane.igs', ents)
golden['synthetic/iges-bounded-plane.igs'] = measure(f'{root}/iges-bounded-plane.igs')

# 112: a single cubic segment (OpenCASCADE reads only the first of several)
coef = [0.5, 1.0, -0.4, 0.2, -1.0, 0.3, 0.8, -0.1, 2.0, -0.5, 0.25, 0.05]
write_iges(f'{root}/iges-spline-curve.igs', [{'type': 112, 'params': [3, 3, 3, 1, 0.0, 2.0] + coef + [0.0] * 12}])
golden['synthetic/iges-spline-curve.igs'] = measure(f'{root}/iges-spline-curve.igs')

with open(golden_path, 'w') as f:
    json.dump(golden, f, indent=1)
    f.write('\n')
