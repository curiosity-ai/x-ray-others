#!/usr/bin/env python3
"""Writes dataset/synthetic/rich.usdc and rich.usdz (a USDZ whose root layer is a crate) with Pixar's USD library (pip install
usd-core), and the expected per-mesh world bounds, triangle counts and display colours, as computed by that library, to
tests/XRay.ThreeD.Tests/Corpus/usdc-golden.json. The meshes are large enough for the crate's compressed integer and float arrays.

    /tmp/venv/bin/python tools/fixtures/make_usdc_fixtures.py dataset/synthetic tests/XRay.ThreeD.Tests/Corpus/usdc-golden.json
"""
import json, math, os, random, sys
from pxr import Usd, UsdGeom, UsdShade, Sdf, Gf, Vt, UsdUtils

out, golden = sys.argv[1], sys.argv[2]
rnd = random.Random(5)
stage = Usd.Stage.CreateInMemory()
UsdGeom.SetStageUpAxis(stage, UsdGeom.Tokens.y)
UsdGeom.SetStageMetersPerUnit(stage, 1.0)

root = UsdGeom.Xform.Define(stage, '/Root')
root.AddTransformOp().Set(Gf.Matrix4d().SetRotate(Gf.Rotation(Gf.Vec3d(0, 1, 0), 30)) * Gf.Matrix4d().SetTranslate(Gf.Vec3d(1.5, 0.25, -2)))
stage.SetDefaultPrim(root.GetPrim())

arm = UsdGeom.Xform.Define(stage, '/Root/Arm')
arm.AddTranslateOp().Set(Gf.Vec3d(0.3, 1.1, 0.7))
arm.AddOrientOp().Set(Gf.Quatf(Gf.Rotation(Gf.Vec3d(1, 2, 3).GetNormalized(), 40).GetQuat()))
arm.AddScaleOp().Set(Gf.Vec3f(1.5, 0.5, 2))

mats = {}
for name, color in (('Paint', (0.8, 0.1, 0.2)), ('Glass', (0.7, 0.9, 1.0))):
    m = UsdShade.Material.Define(stage, '/Looks/' + name)
    sh = UsdShade.Shader.Define(stage, '/Looks/%s/PBR' % name)
    sh.CreateIdAttr('UsdPreviewSurface')
    sh.CreateInput('diffuseColor', Sdf.ValueTypeNames.Color3f).Set(Gf.Vec3f(*color))
    sh.CreateInput('roughness', Sdf.ValueTypeNames.Float).Set(0.4)
    sh.CreateInput('opacity', Sdf.ValueTypeNames.Float).Set(0.5 if name == 'Glass' else 1.0)
    m.CreateSurfaceOutput().ConnectToSource(sh.ConnectableAPI(), 'surface')
    mats[name] = m

def grid(path, n, jitter):
    mesh = UsdGeom.Mesh.Define(stage, path)
    pts = [Gf.Vec3f(x * 0.1, rnd.uniform(0, jitter), z * 0.1) for z in range(n + 1) for x in range(n + 1)]
    idx, cnt = [], []
    for z in range(n):
        for x in range(n):
            a = z * (n + 1) + x
            idx += [a, a + 1, a + n + 2, a + n + 1]
            cnt.append(4)
    mesh.CreatePointsAttr(pts)
    mesh.CreateFaceVertexCountsAttr(cnt)
    mesh.CreateFaceVertexIndicesAttr(idx)
    return mesh

g = grid('/Root/Arm/Terrain', 20, 0.3)
g.CreateDisplayColorPrimvar(UsdGeom.Tokens.constant).Set([(0.2, 0.6, 0.3)])
g.GetPrim().SetDocumentation('terrain')
UsdShade.MaterialBindingAPI.Apply(g.GetPrim()).Bind(mats['Paint'])

h = grid('/Root/Plate', 6, 0.0)
UsdGeom.Xformable(h).AddTranslateOp().Set(Gf.Vec3d(0, -1, 0.123456))
sub_a = UsdGeom.Subset.Define(stage, '/Root/Plate/Left')
sub_a.CreateElementTypeAttr('face')
sub_a.CreateIndicesAttr([i for i in range(36) if i % 6 < 3])
UsdShade.MaterialBindingAPI.Apply(sub_a.GetPrim()).Bind(mats['Glass'])
sub_b = UsdGeom.Subset.Define(stage, '/Root/Plate/Right')
sub_b.CreateElementTypeAttr('face')
sub_b.CreateIndicesAttr([i for i in range(36) if i % 6 >= 3])
UsdShade.MaterialBindingAPI.Apply(sub_b.GetPrim()).Bind(mats['Paint'])
h.CreateDisplayColorPrimvar(UsdGeom.Tokens.vertex).Set([(rnd.random(), rnd.random(), rnd.random()) for _ in range(49)])

cyl = UsdGeom.Cylinder.Define(stage, '/Root/Pipe')
cyl.CreateRadiusAttr(0.2); cyl.CreateHeightAttr(3); cyl.CreateAxisAttr('X')
UsdGeom.Xformable(cyl).AddRotateYXZOp().Set(Gf.Vec3f(10, 20, 30))

path = os.path.join(out, 'rich.usdc')
stage.GetRootLayer().Export(path)
usda = os.path.join(out, '_rich.usda')
stage.GetRootLayer().Export(usda)
os.remove(usda)
tmp = os.path.join(out, 'rich-root.usdc')
os.replace(path, tmp)
UsdUtils.CreateNewUsdzPackage(tmp, os.path.join(out, 'rich.usdz'))
os.replace(tmp, path)

result = {}
for prim in stage.Traverse():
    if prim.IsA(UsdGeom.Mesh):
        m = UsdGeom.Mesh(prim)
        counts = m.GetFaceVertexCountsAttr().Get()
        xf = UsdGeom.Xformable(prim).ComputeLocalToWorldTransform(Usd.TimeCode.Default())
        world = [xf.Transform(Gf.Vec3d(*p)) for p in m.GetPointsAttr().Get()]
        result[prim.GetName()] = {
            'triangles': int(sum(max(c - 2, 0) for c in counts)),
            'min': [round(min(p[i] for p in world), 4) for i in range(3)], 'max': [round(max(p[i] for p in world), 4) for i in range(3)],
            'sum': [round(sum(p[i] for p in world), 3) for i in range(3)],
        }
result['_meta'] = {'upAxis': 'Y', 'meshes': ['Terrain', 'Plate'], 'looks': ['Paint', 'Glass'], 'terrainColor': [0.2, 0.6, 0.3]}
json.dump(result, open(golden, 'w'), indent=1)
print(json.dumps(result)[:400])
