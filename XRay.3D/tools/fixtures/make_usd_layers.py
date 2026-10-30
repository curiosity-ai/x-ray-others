#!/usr/bin/env python3
"""A USD scene spread over several layers (written with Pixar's USD library, pip install usd-core): a root layer with a sublayer, a reference to
a geometry layer, a payload, a variant selection authored on a referencing prim, an animated mesh (the first time sample counts) and a quad
with a UsdUVTexture material, in dataset/synthetic/usd-layers/ as loose files and packed into dataset/synthetic/usd-layers.usdz. The golden file
holds what pxr composes (world bounds, triangle count and world surface area at the earliest time sample, which is the value the reader takes).

    /tmp/claude-0/ezvenv/bin/python tools/fixtures/make_usd_layers.py dataset/synthetic tests/XRay.ThreeD.Tests/Corpus/usd-layers-golden.json
"""
import json, os, struct, sys, zlib, zipfile
from pxr import Usd, UsdGeom, UsdShade, Sdf, Gf, Vt

out, golden = sys.argv[1], sys.argv[2]
d = os.path.join(out, 'usd-layers')
os.makedirs(os.path.join(d, 'geom'), exist_ok=True)
os.makedirs(os.path.join(d, 'textures'), exist_ok=True)


def new(path, up='Y'):
    stage = Usd.Stage.CreateNew(path)
    UsdGeom.SetStageUpAxis(stage, UsdGeom.Tokens.y)
    UsdGeom.SetStageMetersPerUnit(stage, 1.0)
    return stage


# a 4 x 4 checker PNG
def png(path, w=4, h=4):
    rows = b''
    for y in range(h):
        rows += b'\x00' + b''.join((b'\xff\x40\x40' if (x + y) % 2 else b'\x40\x40\xff') for x in range(w))
    def chunk(t, data): return struct.pack('>I', len(data)) + t + data + struct.pack('>I', zlib.crc32(t + data) & 0xffffffff)
    open(path, 'wb').write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(rows)) + chunk(b'IEND', b''))


png(os.path.join(d, 'textures', 'checker.png'))

# geom/box.usda: a box mesh
s = new(os.path.join(d, 'geom', 'box.usda'))
box = UsdGeom.Mesh.Define(s, '/Box')
box.CreatePointsAttr([(0, 0, 0), (2, 0, 0), (2, 1, 0), (0, 1, 0), (0, 0, 3), (2, 0, 3), (2, 1, 3), (0, 1, 3)])
box.CreateFaceVertexCountsAttr([4] * 6)
box.CreateFaceVertexIndicesAttr([0, 3, 2, 1, 4, 5, 6, 7, 0, 1, 5, 4, 1, 2, 6, 5, 2, 3, 7, 6, 3, 0, 4, 7])
s.SetDefaultPrim(box.GetPrim())
s.GetRootLayer().Save()

# geom/ball.usda: a payload with a translated cube
s = new(os.path.join(d, 'geom', 'ball.usda'))
cube = UsdGeom.Cube.Define(s, '/Ball')
cube.CreateSizeAttr(1.5)
cube.AddTranslateOp().Set(Gf.Vec3d(0, 4, 0))
s.SetDefaultPrim(cube.GetPrim())
s.GetRootLayer().Save()

# geom/variants.usda: /Thing with a variant set "shape" whose choices add a cube of different size
s = new(os.path.join(d, 'geom', 'variants.usda'))
thing = UsdGeom.Xform.Define(s, '/Thing')
vs = thing.GetPrim().GetVariantSets().AddVariantSet('shape')
for name, size in (('short', 1.0), ('tall', 3.0)):
    vs.AddVariant(name)
    vs.SetVariantSelection(name)
    with vs.GetVariantEditContext():
        c = UsdGeom.Cube.Define(s, '/Thing/Body')
        c.CreateSizeAttr(size)
vs.SetVariantSelection('short')
s.SetDefaultPrim(thing.GetPrim())
s.GetRootLayer().Save()

# sub.usda: a sublayer that adds a cube under /World
s = new(os.path.join(d, 'sub.usda'))
over = s.OverridePrim('/World')
c = UsdGeom.Cube.Define(s, '/World/FromSubLayer')
c.CreateSizeAttr(0.5)
c.AddTranslateOp().Set(Gf.Vec3d(-3, 0, 0))
s.GetRootLayer().Save()

# root.usda
s = new(os.path.join(d, 'root.usda'))
s.GetRootLayer().subLayerPaths.append('./sub.usda')
world = UsdGeom.Xform.Define(s, '/World')
s.SetDefaultPrim(world.GetPrim())
# typeless prims take the type of what they reference (a Mesh and a Cube), so the geometry shows up under the referencing prim
a = s.DefinePrim('/World/RefBox')
a.GetReferences().AddReference('./geom/box.usda')
a.CreateAttribute('xformOp:translate', Sdf.ValueTypeNames.Double3).Set(Gf.Vec3d(10, 0, 0))
a.CreateAttribute('xformOpOrder', Sdf.ValueTypeNames.TokenArray, custom=False).Set(Vt.TokenArray(['xformOp:translate']))
b = s.DefinePrim('/World/PayloadBall')
b.GetPayloads().AddPayload('./geom/ball.usda')
v = UsdGeom.Xform.Define(s, '/World/Variants')
v.GetPrim().GetReferences().AddReference('./geom/variants.usda')
v.GetPrim().GetVariantSet('shape').SetVariantSelection('tall')
v.AddTranslateOp().Set(Gf.Vec3d(0, 0, 8))
anim = UsdGeom.Mesh.Define(s, '/World/Anim')
anim.CreateFaceVertexCountsAttr([3])
anim.CreateFaceVertexIndicesAttr([0, 1, 2])
pts = anim.CreatePointsAttr()
pts.Set([(0, 0, 0), (1, 0, 0), (0, 1, 0)], Usd.TimeCode(5))
pts.Set([(0, 0, 0), (4, 0, 0), (0, 4, 0)], Usd.TimeCode(10))
anim.AddTranslateOp().Set(Gf.Vec3d(0, 0, -6))
quad = UsdGeom.Mesh.Define(s, '/World/Quad')
quad.CreatePointsAttr([(0, 0, 0), (2, 0, 0), (2, 2, 0), (0, 2, 0)])
quad.CreateFaceVertexCountsAttr([4])
quad.CreateFaceVertexIndicesAttr([0, 1, 2, 3])
st = UsdGeom.PrimvarsAPI(quad).CreatePrimvar('st', Sdf.ValueTypeNames.TexCoord2fArray, UsdGeom.Tokens.faceVarying)
st.Set([(0, 0), (1, 0), (1, 1), (0, 1)])
quad.AddTranslateOp().Set(Gf.Vec3d(0, -5, 0))
mat = UsdShade.Material.Define(s, '/World/Looks/Checker')
pbr = UsdShade.Shader.Define(s, '/World/Looks/Checker/PBR')
pbr.CreateIdAttr('UsdPreviewSurface')
tex = UsdShade.Shader.Define(s, '/World/Looks/Checker/Tex')
tex.CreateIdAttr('UsdUVTexture')
tex.CreateInput('file', Sdf.ValueTypeNames.Asset).Set('./textures/checker.png')
tex.CreateOutput('rgb', Sdf.ValueTypeNames.Float3)
pbr.CreateInput('diffuseColor', Sdf.ValueTypeNames.Color3f).ConnectToSource(tex.ConnectableAPI(), 'rgb')
mat.CreateSurfaceOutput().ConnectToSource(pbr.ConnectableAPI(), 'surface')
UsdShade.MaterialBindingAPI.Apply(quad.GetPrim()).Bind(mat)
s.GetRootLayer().Save()

# what pxr composes
stage = Usd.Stage.Open(os.path.join(d, 'root.usda'))
T = Usd.TimeCode.EarliestTime()  # pxr has no value at the default time for attributes that only have samples
cache = UsdGeom.XformCache(T)
lo = [1e30] * 3
hi = [-1e30] * 3
tris = 0
area = 0.0
names = []
for prim in stage.Traverse():
    if prim.IsA(UsdGeom.Mesh):
        m = UsdGeom.Mesh(prim)
        pts = m.GetPointsAttr().Get(T)
        counts = m.GetFaceVertexCountsAttr().Get(T)
        idx = m.GetFaceVertexIndicesAttr().Get(T)
        world = cache.GetLocalToWorldTransform(prim)
        wp = [world.Transform(Gf.Vec3d(*p)) for p in pts]
        c = 0
        for n in counts:
            for k in range(1, n - 1):
                a_, b_, c_ = wp[idx[c]], wp[idx[c + k]], wp[idx[c + k + 1]]
                area += 0.5 * ((b_ - a_) ^ (c_ - a_)).GetLength()
                tris += 1
            c += n
    elif prim.IsA(UsdGeom.Cube):
        size = UsdGeom.Cube(prim).GetSizeAttr().Get(T)
        world = cache.GetLocalToWorldTransform(prim)
        h = size / 2
        wp = [world.Transform(Gf.Vec3d(x, y, z)) for x in (-h, h) for y in (-h, h) for z in (-h, h)]
        tris += 12
        area += 6 * size * size  # a rigid transform
    else:
        continue
    names.append(prim.GetPath().pathString)
    for p in wp:
        for i in range(3):
            lo[i] = min(lo[i], p[i]); hi[i] = max(hi[i], p[i])
json.dump({'prims': names, 'min': lo, 'max': hi, 'triangles': tris, 'area': area}, open(golden, 'w'), indent=1)

# a binary (crate) root layer with a sublayer and a reference to the text geometry layers: composed by pxr, and read from the crate by the reader
s = new(os.path.join(d, 'crate-root.usdc'))
s.GetRootLayer().subLayerPaths.append('./sub.usda')
world = UsdGeom.Xform.Define(s, '/World')
s.SetDefaultPrim(world.GetPrim())
a = s.DefinePrim('/World/RefBox')
a.GetReferences().AddReference('./geom/box.usda')
a.CreateAttribute('xformOp:translate', Sdf.ValueTypeNames.Double3).Set(Gf.Vec3d(10, 0, 0))
a.CreateAttribute('xformOpOrder', Sdf.ValueTypeNames.TokenArray, custom=False).Set(Vt.TokenArray(['xformOp:translate']))
s.GetRootLayer().Save()
stage = Usd.Stage.Open(os.path.join(d, 'crate-root.usdc'))
cache = UsdGeom.XformCache(Usd.TimeCode.EarliestTime())
lo = [1e30] * 3; hi = [-1e30] * 3; tris = 0; area = 0.0
for prim in stage.Traverse():
    if prim.IsA(UsdGeom.Mesh):
        m = UsdGeom.Mesh(prim); pts = m.GetPointsAttr().Get(); counts = m.GetFaceVertexCountsAttr().Get(); idx = m.GetFaceVertexIndicesAttr().Get()
        world = cache.GetLocalToWorldTransform(prim); wp = [world.Transform(Gf.Vec3d(*p)) for p in pts]
        c = 0
        for n in counts:
            for k in range(1, n - 1):
                area += 0.5 * ((wp[idx[c + k]] - wp[idx[c]]) ^ (wp[idx[c + k + 1]] - wp[idx[c]])).GetLength(); tris += 1
            c += n
    elif prim.IsA(UsdGeom.Cube):
        size = UsdGeom.Cube(prim).GetSizeAttr().Get(); world = cache.GetLocalToWorldTransform(prim); h = size / 2
        wp = [world.Transform(Gf.Vec3d(x, y, z)) for x in (-h, h) for y in (-h, h) for z in (-h, h)]; tris += 12; area += 6 * size * size
    else:
        continue
    for p in wp:
        for i in range(3):
            lo[i] = min(lo[i], p[i]); hi[i] = max(hi[i], p[i])
g = json.load(open(golden))
g['crate'] = {'min': lo, 'max': hi, 'triangles': tris, 'area': area}
json.dump(g, open(golden, 'w'), indent=1)

# the same scene as a package: members stored uncompressed, root layer first
with zipfile.ZipFile(os.path.join(out, 'usd-layers.usdz'), 'w', zipfile.ZIP_STORED) as z:
    for rel in ('root.usda', 'sub.usda', 'geom/box.usda', 'geom/ball.usda', 'geom/variants.usda', 'textures/checker.png'):
        z.write(os.path.join(d, rel), rel)
