#!/usr/bin/env python3
"""Writes dataset/synthetic/scene.usda, scene.usdc and scene.usdz with Pixar's USD library (pip install usd-core), and the
expected world bounds and triangle count computed by that library, to tests/XRay.ThreeD.Tests/Corpus/usd-golden.json.

    /tmp/venv/bin/python tools/fixtures/make_usd_fixtures.py dataset/synthetic tests/XRay.ThreeD.Tests/Corpus/usd-golden.json
"""
import json, os, sys
from pxr import Usd, UsdGeom, UsdShade, Sdf, Gf, Vt, UsdUtils

out, golden = sys.argv[1], sys.argv[2]
path = os.path.join(out, 'scene.usda')
stage = Usd.Stage.CreateInMemory()
UsdGeom.SetStageUpAxis(stage, UsdGeom.Tokens.z)
UsdGeom.SetStageMetersPerUnit(stage, 0.01)

world = UsdGeom.Xform.Define(stage, '/World')
world.AddTranslateOp().Set(Gf.Vec3d(1, 2, 3))
world.AddRotateXYZOp().Set(Gf.Vec3f(0, 0, 90))
world.AddScaleOp().Set(Gf.Vec3f(2, 2, 2))
stage.SetDefaultPrim(world.GetPrim())

V = [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0), (0, 0, 1), (1, 0, 1), (1, 1, 1), (0, 1, 1)]
Q = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
cube = UsdGeom.Mesh.Define(stage, '/World/Cube')
cube.CreatePointsAttr([Gf.Vec3f(*v) for v in V])
cube.CreateFaceVertexCountsAttr([4] * 6)
cube.CreateFaceVertexIndicesAttr([i for q in Q for i in q])
cube.CreateDisplayColorPrimvar(UsdGeom.Tokens.uniform).Set([(1, 0, 0)] * 3 + [(0, 1, 0)] * 3)

mats = {}
for name, color, metallic in (('Red', (1, 0, 0), 0.0), ('Steel', (0.6, 0.6, 0.65), 1.0)):
    m = UsdShade.Material.Define(stage, '/Materials/' + name)
    shader = UsdShade.Shader.Define(stage, '/Materials/%s/Shader' % name)
    shader.CreateIdAttr('UsdPreviewSurface')
    shader.CreateInput('diffuseColor', Sdf.ValueTypeNames.Color3f).Set(Gf.Vec3f(*color))
    shader.CreateInput('metallic', Sdf.ValueTypeNames.Float).Set(metallic)
    shader.CreateInput('roughness', Sdf.ValueTypeNames.Float).Set(0.25)
    m.CreateSurfaceOutput().ConnectToSource(shader.ConnectableAPI(), 'surface')
    mats[name] = m

pyramid = UsdGeom.Mesh.Define(stage, '/World/Pyramid')
pyramid.AddTranslateOp().Set(Gf.Vec3d(5, 0, 0))
pyramid.CreatePointsAttr([Gf.Vec3f(*v) for v in [(0, 0, 0), (2, 0, 0), (2, 2, 0), (0, 2, 0), (1, 1, 3)]])
pyramid.CreateFaceVertexCountsAttr([4, 3, 3, 3, 3])
pyramid.CreateFaceVertexIndicesAttr([0, 3, 2, 1, 0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4])
UsdShade.MaterialBindingAPI.Apply(pyramid.GetPrim()).Bind(mats['Steel'])
pyramid.CreateNormalsAttr([Gf.Vec3f(0, 0, -1)] * 4 + [Gf.Vec3f(0, -1, 0.5)] * 3 + [Gf.Vec3f(1, 0, 0.5)] * 3 + [Gf.Vec3f(0, 1, 0.5)] * 3 + [Gf.Vec3f(-1, 0, 0.5)] * 3)
pyramid.SetNormalsInterpolation(UsdGeom.Tokens.faceVarying)

ball = UsdGeom.Sphere.Define(stage, '/World/Ball')
ball.CreateRadiusAttr(0.5)
ball.AddTranslateOp().Set(Gf.Vec3d(0, 0, 4))
UsdShade.MaterialBindingAPI.Apply(ball.GetPrim()).Bind(mats['Red'])

copy = stage.OverridePrim('/Copy')
copy.SetSpecifier(Sdf.SpecifierDef)
copy.GetReferences().AddInternalReference('/World/Cube')
UsdGeom.Xformable(copy).AddTranslateOp().Set(Gf.Vec3d(0, -10, 0))

# The root layer is exported as authored (Stage.Export would flatten the reference away).
stage.GetRootLayer().Export(path)
stage.GetRootLayer().Export(os.path.join(out, 'scene.usdc'))
UsdUtils.CreateNewUsdzPackage(path, os.path.join(out, 'scene.usdz'))

cache = UsdGeom.BBoxCache(Usd.TimeCode.Default(), [UsdGeom.Tokens.default_])
box = cache.ComputeWorldBound(stage.GetPseudoRoot()).ComputeAlignedRange()
# Triangles: the cube is 6 quads, the pyramid a quad and four triangles; the sphere is tessellated by the reader.
json.dump({'scene.usda': {
    'metersPerUnit': 0.01, 'upAxis': 'Z',
    'min': [round(float(v), 4) for v in box.GetMin()], 'max': [round(float(v), 4) for v in box.GetMax()],
    'primitives': {'cube': 12, 'pyramid': 6, 'copy': 12}}}, open(golden, 'w'), indent=1)
print('world bounds in file units:', box.GetMin(), box.GetMax())
