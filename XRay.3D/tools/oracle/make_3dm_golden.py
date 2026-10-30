#!/usr/bin/env python3
"""Expected values for the Rhino 3DM tests from an independent reader (rhino3dm, MIT, the openNURBS bindings).

    /tmp/ezvenv/bin/python tools/oracle/make_3dm_golden.py dataset/3dm dataset/synthetic > tests/XRay.ThreeD.Tests/Corpus/rhino-golden.json
    # local-only samples (never committed): counts and extents only, no authors
    /tmp/ezvenv/bin/python tools/oracle/make_3dm_golden.py /tmp/claude-0/samples/rhino --no-authors > tests/XRay.ThreeD.Tests/Corpus/rhino-local-golden.json

Per file: units, tolerance, layers (name, visibility, colour), materials, block definitions, and per object its kind, name,
layer, user strings and the exact counts and extents of its geometry (meshes: vertices, triangles; curves: the control or
polyline points).
"""
import glob, json, os, sys
import rhino3dm as r


def box(points):
    xs = [p[0] for p in points]; ys = [p[1] for p in points]; zs = [p[2] for p in points]
    return [round(min(xs), 4), round(min(ys), 4), round(min(zs), 4), round(max(xs), 4), round(max(ys), 4), round(max(zs), 4)] if points else None


def points_of(g):
    if isinstance(g, r.Mesh):
        return [(v.X, v.Y, v.Z) for v in g.Vertices]
    if isinstance(g, r.PolylineCurve):
        return [(g.Point(i).X, g.Point(i).Y, g.Point(i).Z) for i in range(g.PointCount)]
    if isinstance(g, r.LineCurve):
        return [(g.PointAtStart.X, g.PointAtStart.Y, g.PointAtStart.Z), (g.PointAtEnd.X, g.PointAtEnd.Y, g.PointAtEnd.Z)]
    if isinstance(g, r.Point):
        return [(g.Location.X, g.Location.Y, g.Location.Z)]
    if isinstance(g, r.PointCloud):
        return [(p.X, p.Y, p.Z) for p in g.GetPoints()]
    return []


def describe(o):
    g = o.Geometry
    a = o.Attributes
    d = {
        'kind': type(g).__name__,
        'name': a.Name,
        'layer': a.LayerIndex,
        'mode': str(a.Mode),
        'visible': bool(a.Visible),
        'colorSource': str(a.ColorSource),
        'materialIndex': a.MaterialIndex,
        'materialSource': str(a.MaterialSource),
        'userStrings': {k: v for k, v in a.GetUserStrings()} if hasattr(a, 'GetUserStrings') else {},
    }
    if not isinstance(g, r.InstanceReference):
        pts = points_of(g)
        d['points'] = len(pts)
        d['box'] = box(pts)
    if isinstance(g, r.Brep):
        tri = vert = 0
        for face in g.Faces:
            me = face.GetMesh(r.MeshType.Default)
            if me:
                tri += sum(2 if me.Faces[k][2] != me.Faces[k][3] else 1 for k in range(len(me.Faces)))
                vert += len(me.Vertices)
        d['renderTriangles'] = tri
        d['renderVertices'] = vert
    if isinstance(g, r.ArcCurve):
        bb = g.GetBoundingBox()
        d['box'] = [round(v, 4) for v in (bb.Min.X, bb.Min.Y, bb.Min.Z, bb.Max.X, bb.Max.Y, bb.Max.Z)]
    if isinstance(g, r.Mesh):
        d['vertices'] = len(g.Vertices)
        d['triangles'] = sum(2 if f[2] != f[3] else 1 for f in (g.Faces[i] for i in range(len(g.Faces))))
        d['normals'] = len(g.Normals)
        d['colors'] = len(g.VertexColors)
    if isinstance(g, r.InstanceReference):
        d['block'] = str(g.ParentIdefId)
        x = g.Xform
        d['xform'] = [round(getattr(x, 'M%d%d' % (i, j)), 6) for i in range(4) for j in range(4)]
    return d


anonymous = '--no-authors' in sys.argv
out = {}
for path in sorted(p for d in sys.argv[1:] if not d.startswith('--') for p in glob.glob(os.path.join(d, '*.3dm'))):
    m = r.File3dm.Read(path)
    s = m.Settings
    out[os.path.basename(path)] = {
        'version': m.ArchiveVersion,
        'units': str(s.ModelUnitSystem),
        'absoluteTolerance': s.ModelAbsoluteTolerance,
        'createdBy': '' if anonymous else m.CreatedBy,
        'revision': m.Revision,
        'layers': [{'name': l.Name, 'index': l.Index, 'visible': bool(l.Visible), 'locked': bool(l.Locked), 'color': list(l.Color), 'id': str(l.Id), 'parent': str(l.ParentLayerId)} for l in m.Layers],
        'materials': [{'name': x.Name, 'diffuse': list(x.DiffuseColor), 'transparency': round(x.Transparency, 6), 'shine': round(x.Shine, 4)} for x in m.Materials],
        'blocks': [{'id': str(b.Id), 'name': b.Name, 'description': b.Description, 'members': len(list(b.GetObjectIds()))} for b in m.InstanceDefinitions],
        'objects': [describe(o) for o in m.Objects],
    }
print(json.dumps(out, indent=1))
