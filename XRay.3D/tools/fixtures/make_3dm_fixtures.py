#!/usr/bin/env python3
"""Writes the Rhino 3DM fixtures of dataset/synthetic with rhino3dm (MIT, openNURBS bindings), archive version 7.

    python3 -m venv /tmp/ezvenv && /tmp/ezvenv/bin/pip install rhino3dm
    /tmp/ezvenv/bin/python tools/fixtures/make_3dm_fixtures.py dataset/synthetic
"""
import math, os, sys
import rhino3dm as r

out = sys.argv[1]
os.makedirs(out, exist_ok=True)


def attrs(layer, name='', **kw):
    a = r.ObjectAttributes()
    a.LayerIndex = layer
    a.Name = name
    for k, v in kw.items():
        setattr(a, k, v)
    return a


def layer(m, name, color, parent=None, visible=True, locked=False):
    l = r.Layer()
    l.Name = name
    l.Color = color
    l.Visible = visible
    l.Locked = locked
    if parent is not None:
        l.ParentLayerId = m.Layers[parent].Id
    m.Layers.Add(l)
    return len(m.Layers) - 1


# ---- scene: every supported object kind, layers, a material, a block with two references
m = r.File3dm()
m.Settings.ModelUnitSystem = r.UnitSystem.Inches
m.Settings.ModelAbsoluteTolerance = 0.01
top = layer(m, 'Top', (255, 0, 0, 255))
sub = layer(m, 'Sub', (0, 255, 0, 255), parent=top)
mat = r.Material()
mat.Name = 'Steel'
mat.DiffuseColor = (200, 100, 50, 255)
mat.Transparency = 0.25
mat.Shine = 60
m.Materials.Add(mat)
m.Objects.AddPoint(r.Point3d(1, 2, 3), attrs(top, 'pt'))
m.Objects.AddLine(r.Point3d(0, 0, 0), r.Point3d(1, 1, 1), attrs(sub, 'line'))
m.Objects.AddCircle(r.Circle(r.Point3d(1, 1, 0), 2.0), attrs(sub, 'circle'))
m.Objects.AddArc(r.Arc(r.Point3d(0, 0, 0), 1.0, 1.5), attrs(sub, 'arc'))
pl = r.Polyline(0)
pl.Add(0, 0, 0)
pl.Add(1, 0, 0)
pl.Add(1, 1, 0)
m.Objects.AddPolyline(pl, attrs(top, 'poly', ObjectColor=(10, 20, 30, 255), ColorSource=r.ObjectColorSource.ColorFromObject))
nc = r.NurbsCurve(3, False, 4, 4)
for i, p in enumerate([(0, 0, 0), (1, 2, 0), (2, 2, 0), (3, 0, 0)]):
    nc.Points[i] = r.Point4d(p[0], p[1], p[2], 1.0)
for i, k in enumerate([0, 0, 0, 1, 1, 1]):
    nc.Knots[i] = k
m.Objects.AddCurve(nc, attrs(top, 'nurbs'))
m.Objects.AddPointCloud(r.PointCloud([r.Point3d(1, 1, 1), r.Point3d(2, 2, 2)]), attrs(top, 'cloud'))
msh = r.Mesh()
for p in [(0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0), (0.5, 0.5, 1)]:
    msh.Vertices.Add(*p)
msh.Faces.AddFace(0, 1, 2, 3)
msh.Faces.AddFace(0, 1, 4)
msh.Normals.ComputeNormals()
for i in range(5):
    msh.VertexColors.Add(10 * i, 20, 30)
msh.Compact()
mesh_attr = attrs(sub, 'mesh', MaterialIndex=0, MaterialSource=r.ObjectMaterialSource.MaterialFromObject)
mesh_attr.SetUserString('part-number', 'A-100')
mesh_attr.SetUserString('note', 'two strings')
m.Objects.AddMesh(msh, mesh_attr)
bm = r.Mesh()
for p in [(0, 0, 0), (2, 0, 0), (0, 2, 0)]:
    bm.Vertices.Add(*p)
bm.Faces.AddFace(0, 1, 2)
bm.Normals.ComputeNormals()
idx = m.InstanceDefinitions.Add('Block1', 'desc', '', '', r.Point3d(0, 0, 0), (bm, r.LineCurve(r.Point3d(0, 0, 0), r.Point3d(3, 3, 3))), (attrs(top, 'bm'), attrs(top, 'bl')))
bid = m.InstanceDefinitions[idx].Id
m.Objects.AddInstanceObject(r.InstanceReference(bid, r.Transform.Translation(10, 0, 0)), attrs(top, 'ref1'))
rot = r.Transform.Rotation(math.pi / 2, r.Vector3d(0, 0, 1), r.Point3d(0, 0, 0))
m.Objects.AddInstanceObject(r.InstanceReference(bid, r.Transform.Multiply(r.Transform.Translation(20, 5, 0), rot)), attrs(sub, 'ref2'))
m.Write(os.path.join(out, 'rhino-scene.3dm'), 7)

# ---- a big mesh: more than 65536 vertices (4-byte indices), compressed buffers, vertex colours
n = 257
g = r.Mesh()
for j in range(n):
    for i in range(n):
        g.Vertices.Add(i * 0.5, j * 0.5, math.sin(i / 9.0) * math.cos(j / 7.0))
for j in range(n - 1):
    for i in range(n - 1):
        g.Faces.AddFace(j * n + i, j * n + i + 1, (j + 1) * n + i + 1, (j + 1) * n + i)
for k in range(n * n):
    g.VertexColors.Add(k % 256, (k // 256) % 256, 128)
g.Compact()
m = r.File3dm()
m.Settings.ModelUnitSystem = r.UnitSystem.Millimeters
lay = layer(m, 'Grid', (40, 80, 160, 255))
m.Objects.AddMesh(g, attrs(lay, 'grid'))
m.Write(os.path.join(out, 'rhino-bigmesh.3dm'), 7)

# ---- layers, hidden and locked content
m = r.File3dm()
m.Settings.ModelUnitSystem = r.UnitSystem.Meters
a = layer(m, 'Visible', (255, 255, 0, 255))
b = layer(m, 'Off', (0, 0, 255, 255), visible=False)
c = layer(m, 'Child of off', (0, 255, 255, 255), parent=b)
d = layer(m, 'Locked', (128, 128, 128, 255), locked=True)
m.Objects.AddLine(r.Point3d(0, 0, 0), r.Point3d(5, 0, 0), attrs(a, 'on'))
m.Objects.AddLine(r.Point3d(0, 1, 0), r.Point3d(5, 1, 0), attrs(b, 'off'))
m.Objects.AddLine(r.Point3d(0, 2, 0), r.Point3d(5, 2, 0), attrs(c, 'inherits off'))
m.Objects.AddLine(r.Point3d(0, 3, 0), r.Point3d(5, 3, 0), attrs(d, 'locked'))
hid = attrs(a, 'hidden object')
hid.Mode = r.ObjectMode.Hidden
m.Objects.AddLine(r.Point3d(0, 4, 0), r.Point3d(5, 4, 0), hid)
m.Write(os.path.join(out, 'rhino-layers.3dm'), 7)
