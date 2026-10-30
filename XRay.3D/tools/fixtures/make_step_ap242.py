#!/usr/bin/env python3
"""AP242 tessellated and mapped-item STEP fixtures, with exact values from OpenCASCADE (pip install cadquery-ocp).

    python3 -I tools/fixtures/make_step_ap242.py dataset/synthetic tests/XRay.ThreeD.Tests/Corpus/step-ap242-golden.json

tessellated.stp  A plate with a through hole and a sphere on top, meshed by OpenCASCADE, written as one TESSELLATED_SOLID: planar faces
                 as TRIANGULATED_FACE over one shared COORDINATES_LIST through a point index (one normal per face), curved faces as
                 COMPLEX_TRIANGULATED_FACE (real strips and fans, own coordinates, per-vertex normals); a layer and a colour.
mapped.stp       One B-rep box (written by OpenCASCADE) instanced by MAPPED_ITEMs, directly and through a nested representation map,
                 under different targets; layers on the mapped items. Only the assembly representation is a shape definition, so
                 OpenCASCADE's own reader transfers exactly what the instances draw: its area, volume and bounds are the oracle.
The golden file holds the exact area, volume and bounds (OpenCASCADE) of what each file draws.
"""
import json, math, os, re, sys, tempfile
from collections import defaultdict
from OCP.BRepPrimAPI import BRepPrimAPI_MakeBox, BRepPrimAPI_MakeCylinder, BRepPrimAPI_MakeSphere
from OCP.BRepAlgoAPI import BRepAlgoAPI_Cut, BRepAlgoAPI_Fuse
from OCP.BRepMesh import BRepMesh_IncrementalMesh
from OCP.BRep import BRep_Tool
from OCP.TopExp import TopExp_Explorer
from OCP.TopAbs import TopAbs_FACE, TopAbs_REVERSED
from OCP.TopoDS import TopoDS
from OCP.TopLoc import TopLoc_Location
from OCP.BRepAdaptor import BRepAdaptor_Surface
from OCP.GeomAbs import GeomAbs_Plane
from OCP.GProp import GProp_GProps
from OCP.BRepGProp import BRepGProp
from OCP.Bnd import Bnd_Box
from OCP.BRepBndLib import BRepBndLib
from OCP.gp import gp_Pnt, gp_Ax2, gp_Dir
from OCP.STEPControl import STEPControl_Writer, STEPControl_AsIs, STEPControl_Reader
from OCP.IFSelect import IFSelect_RetDone

out, golden_path = sys.argv[1], sys.argv[2]
golden = {}

def props(shape):
    area = GProp_GProps(); BRepGProp.SurfaceProperties_s(shape, area)
    volume = GProp_GProps(); BRepGProp.VolumeProperties_s(shape, volume)
    box = Bnd_Box(); BRepBndLib.AddOptimal_s(shape, box, False, False)
    lo, hi = box.CornerMin(), box.CornerMax()
    return {'area': round(area.Mass(), 4), 'volume': round(volume.Mass(), 4),
            'min': [round(lo.X(), 4), round(lo.Y(), 4), round(lo.Z(), 4)], 'max': [round(hi.X(), 4), round(hi.Y(), 4), round(hi.Z(), 4)]}

def fmt(v):
    return repr(round(v, 9)) if v else '0.'

def real_list(vs):
    return '(' + ','.join(fmt(v) for v in vs) + ')'

CONTEXT = """#9000 = ( GEOMETRIC_REPRESENTATION_CONTEXT(3) GLOBAL_UNIT_ASSIGNED_CONTEXT((#9001,#9002,#9003)) REPRESENTATION_CONTEXT('Context #1','3D Context with UNIT') );
#9001 = ( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.MILLI.,.METRE.) );
#9002 = ( NAMED_UNIT(*) PLANE_ANGLE_UNIT() SI_UNIT($,.RADIAN.) );
#9003 = ( NAMED_UNIT(*) SI_UNIT($,.STERADIAN.) SOLID_ANGLE_UNIT() );"""

def header(name, schema='AP242_MANAGED_MODEL_BASED_3D_ENGINEERING_MIM_LF { 1 0 10303 442 1 1 4 }'):
    return ("ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('%s'),'2;1');\nFILE_NAME('%s','2024-01-01T00:00:00',('x-ray'),('Curiosity GmbH'),'make_step_ap242.py','make_step_ap242.py','');\n"
            "FILE_SCHEMA(('%s'));\nENDSEC;\nDATA;\n") % (name, name, schema)

# ---------------------------------------------------------------- tessellated.stp
plate = BRepPrimAPI_MakeBox(gp_Pnt(0, 0, 0), 20, 10, 6).Shape()
plate = BRepAlgoAPI_Cut(plate, BRepPrimAPI_MakeCylinder(gp_Ax2(gp_Pnt(5, 5, -1), gp_Dir(0, 0, 1)), 2.0, 10).Shape()).Shape()
plate = BRepAlgoAPI_Fuse(plate, BRepPrimAPI_MakeSphere(gp_Pnt(14, 5, 6), 3.0).Shape()).Shape()
BRepMesh_IncrementalMesh(plate, 0.02, False, 0.15, False)
golden['tessellated.stp'] = props(plate)

def face_triangles(face):
    loc = TopLoc_Location()
    tri = BRep_Tool.Triangulation_s(face, loc)
    trsf = loc.Transformation()
    pts = []
    for i in range(1, tri.NbNodes() + 1):
        p = tri.Node(i).Transformed(trsf)
        pts.append((p.X(), p.Y(), p.Z()))
    tris = []
    for i in range(1, tri.NbTriangles() + 1):
        a, b, c = tri.Triangle(i).Get()
        tris.append((a - 1, c - 1, b - 1) if face.Orientation() == TopAbs_REVERSED else (a - 1, b - 1, c - 1))
    return pts, tris

def sub(a, b): return (a[0]-b[0], a[1]-b[1], a[2]-b[2])
def cross(a, b): return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])
def norm(v):
    l = math.sqrt(sum(x*x for x in v)) or 1.0
    return tuple(x / l for x in v)

def vertex_normals(pts, tris):
    acc = [[0.0, 0.0, 0.0] for _ in pts]
    for a, b, c in tris:
        n = cross(sub(pts[b], pts[a]), sub(pts[c], pts[a]))
        for v in (a, b, c):
            for k in range(3): acc[v][k] += n[k]
    return [norm(n) for n in acc]

def strips_and_fans(tris):
    """Greedy strips over shared edges (a strip's odd triangles are wound the other way round); what is left becomes fans."""
    remaining = {t: True for t in tris}
    by_edge = defaultdict(list)
    for t in tris:
        a, b, c = t
        for e in ((a, b), (b, c), (c, a)): by_edge[e].append(t)
    strips, fans = [], []
    for t in tris:
        if not remaining.get(t): continue
        remaining[t] = False
        a, b, c = t
        strip = [a, b, c]
        while True:
            # the next triangle contains the directed edge (strip[-1], strip[-2]) of the last one: (c, b, d)
            last = strip[-1], strip[-2]
            nxt = next((u for u in by_edge[last] if remaining.get(u)), None)
            if nxt is None: break
            remaining[nxt] = False
            d = next(v for v in nxt if v not in last)
            strip.append(d)
        if len(strip) >= 4: strips.append(strip)
        else: fans.append(list(t))
    return strips, fans

ents = []
nid = [100]
def add(text):
    nid[0] += 1
    ents.append('#%d = %s;' % (nid[0], text))
    return nid[0]

faces = []
planar_pts, planar_index, planar_faces = [], {}, []
curved = []
explorer = TopExp_Explorer(plate, TopAbs_FACE)
while explorer.More():
    face = TopoDS.Face_s(explorer.Current()) if hasattr(TopoDS, 'Face_s') else TopoDS.Face(explorer.Current())
    (planar_faces if BRepAdaptor_Surface(face).GetType() == GeomAbs_Plane else curved).append(face)
    explorer.Next()

shared = None
face_ids = []
planar_data = []
for face in planar_faces:
    pts, tris = face_triangles(face)
    local = []
    for p in pts:
        key = tuple(round(x, 7) for x in p)
        if key not in planar_index:
            planar_index[key] = len(planar_pts); planar_pts.append(p)
        local.append(planar_index[key] + 1)
    planar_data.append((pts, tris, local))
shared = add('COORDINATES_LIST(\'\',%d,(%s))' % (len(planar_pts), ','.join(real_list(p) for p in planar_pts)))
for pts, tris, local in planar_data:
    n = norm(cross(sub(pts[tris[0][1]], pts[tris[0][0]]), sub(pts[tris[0][2]], pts[tris[0][0]])))
    face_ids.append(add("TRIANGULATED_FACE('',#%d,%d,(%s),$,(%s),(%s))" % (
        shared, len(pts), real_list(n), ','.join(map(str, local)), ','.join('(%d,%d,%d)' % (a + 1, b + 1, c + 1) for a, b, c in tris))))
for face in curved:
    pts, tris = face_triangles(face)
    coords = add("COORDINATES_LIST('',%d,(%s))" % (len(pts), ','.join(real_list(p) for p in pts)))
    normals = vertex_normals(pts, tris)
    strips, fans = strips_and_fans(tris)
    face_ids.append(add("COMPLEX_TRIANGULATED_FACE('',#%d,%d,(%s),$,(),(%s),(%s))" % (
        coords, len(pts), ','.join(real_list(n) for n in normals),
        ','.join('(' + ','.join(str(v + 1) for v in s) + ')' for s in strips),
        ','.join('(' + ','.join(str(v + 1) for v in f) + ')' for f in fans))))
solid = add("TESSELLATED_SOLID('plate',(%s),$)" % ','.join('#%d' % i for i in face_ids))
axis = add("AXIS2_PLACEMENT_3D('',#%d,#%d,#%d)" % (add("CARTESIAN_POINT('',(0.,0.,0.))"), add("DIRECTION('',(0.,0.,1.))"), add("DIRECTION('',(1.,0.,0.))")))
rep = add("TESSELLATED_SHAPE_REPRESENTATION('',(#%d,#%d),#9000)" % (axis, solid))
ents.append("""#1 = APPLICATION_CONTEXT('Managed model based 3d engineering');
#2 = APPLICATION_PROTOCOL_DEFINITION('international standard','ap242_managed_model_based_3d_engineering',2013,#1);
#3 = PRODUCT_CONTEXT('',#1,'mechanical');
#4 = PRODUCT('P-100','Tessellated plate','',(#3));
#5 = PRODUCT_DEFINITION_FORMATION('A','',#4);
#6 = PRODUCT_DEFINITION_CONTEXT('part definition',#1,'design');
#7 = PRODUCT_DEFINITION('design','',#5,#6);
#8 = PRODUCT_DEFINITION_SHAPE('','',#7);
#9 = SHAPE_DEFINITION_REPRESENTATION(#8,#%d);""" % rep)
colour = add("COLOUR_RGB('',0.8,0.1,0.1)")
fill = add("FILL_AREA_STYLE_COLOUR('',#%d)" % colour)
area_style = add("FILL_AREA_STYLE('',(#%d))" % fill)
surface_fill = add("SURFACE_STYLE_FILL_AREA(#%d)" % area_style)
side = add("SURFACE_SIDE_STYLE('',(#%d))" % surface_fill)
usage = add("SURFACE_STYLE_USAGE(.BOTH.,#%d)" % side)
psa = add("PRESENTATION_STYLE_ASSIGNMENT((#%d))" % usage)
add("STYLED_ITEM('',(#%d),#%d)" % (psa, face_ids[-1]))
add("PRESENTATION_LAYER_ASSIGNMENT('TOP','top layer',(#%d))" % solid)
body = header('tessellated.stp') + CONTEXT + '\n' + '\n'.join(ents) + '\nENDSEC;\nEND-ISO-10303-21;\n'
open(os.path.join(out, 'tessellated.stp'), 'w', newline='\n').write(body)
golden['tessellated.stp']['faces'] = len(face_ids)

# ---------------------------------------------------------------- mapped.stp
box = BRepPrimAPI_MakeBox(4, 3, 2).Shape()
writer = STEPControl_Writer(); writer.Transfer(box, STEPControl_AsIs)
with tempfile.TemporaryDirectory() as tmp:
    path = os.path.join(tmp, 'box.step')
    writer.Write(path)
    text = open(path).read()
data = text[text.index('DATA;') + 5:text.index('ENDSEC;\nEND-ISO')]
data = re.sub(r'^#3 = SHAPE_DEFINITION_REPRESENTATION\(#4,#10\);', '#3 = SHAPE_DEFINITION_REPRESENTATION(#4,#1050);', data, flags=re.M)
ctx = re.search(r"^#10 = ADVANCED_BREP_SHAPE_REPRESENTATION\('',\(#11,#15\),#(\d+)\);", data, flags=re.M).group(1)
# (OpenCASCADE's reader and the standard agree when the origin is only offset, or the target only rotated; they differ for both at once,
# so the oracle fixture avoids that case and the tests state the standard's result for it explicitly.)
# The box's representation (#10, origin placement #11) is only reachable through the map; the assembly (#1050) is the shape definition.
def placement(n, x, y, z, ax, ay, az, rx, ry, rz):
    return ("#%d = CARTESIAN_POINT('',(%s,%s,%s));\n#%d = DIRECTION('',(%s,%s,%s));\n#%d = DIRECTION('',(%s,%s,%s));\n#%d = AXIS2_PLACEMENT_3D('',#%d,#%d,#%d);\n"
            % (n, fmt(x), fmt(y), fmt(z), n + 1, fmt(ax), fmt(ay), fmt(az), n + 2, fmt(rx), fmt(ry), fmt(rz), n + 3, n, n + 1, n + 2))
extra = []
extra.append('#1000 = REPRESENTATION_MAP(#11,#10);')
# a second map: origin offset from its representation's frame, whose representation holds two instances of the box map
extra.append(placement(1010, 0, 0, 0, 0, 0, 1, 1, 0, 0))              # #1013 origin of the pair
extra.append(placement(1020, 10, 0, 0, 0, 0, 1, 1, 0, 0))             # #1023 first box in the pair
extra.append(placement(1030, 0, 8, 0, 0, 0, 1, 0, 1, 0))              # #1033 second box in the pair, turned 90 degrees
extra.append("#1040 = MAPPED_ITEM('boxA',#1000,#1023);")
extra.append("#1041 = MAPPED_ITEM('boxB',#1000,#1033);")
extra.append("#1042 = SHAPE_REPRESENTATION('pair',(#1013,#1040,#1041),#" + ctx + ");")
extra.append('#1043 = REPRESENTATION_MAP(#1013,#1042);')
extra.append(placement(1060, 3, 0, 0, 1, 0, 0, 0, 0, -1))              # #1063 pair instance 1 (stood on its side)
extra.append(placement(1070, 50, 20, 5, 0, 0, 1, 0, 1, 0))            # #1073 pair instance 2 (turned, lifted)
extra.append(placement(1080, -10, 0, 0, 0, 0, 1, 1, 0, 0))            # #1083 a single box, translated
extra.append(placement(1100, 1, 1, 0, 0, 0, 1, 1, 0, 0))             # #1103 an origin off the representation's own
extra.append('#1001 = REPRESENTATION_MAP(#1103,#10);')
extra.append("#1051 = MAPPED_ITEM('pair 1',#1043,#1063);")
extra.append("#1052 = MAPPED_ITEM('pair 2',#1043,#1073);")
extra.append("#1053 = MAPPED_ITEM('lone box',#1001,#1083);")
extra.append("#1054 = AXIS2_PLACEMENT_3D('',#12,#13,#14);")
extra.append("#1050 = SHAPE_REPRESENTATION('',(#1054,#1051,#1052,#1053),#" + ctx + ");")
extra.append("PRESENTATION_LAYER_ASSIGNMENT('PAIRS','',(#1051,#1052));".replace('PRES', '#1090 = PRES'))
extra.append("#1091 = PRESENTATION_LAYER_ASSIGNMENT('LONE','',(#1053));")
text2 = header('mapped.stp', 'AP242_MANAGED_MODEL_BASED_3D_ENGINEERING_MIM_LF { 1 0 10303 442 1 1 4 }') + data.strip('\n') + '\n' + '\n'.join(extra) + '\nENDSEC;\nEND-ISO-10303-21;\n'
open(os.path.join(out, 'mapped.stp'), 'w', newline='\n').write(text2)
reader = STEPControl_Reader()
assert reader.ReadFile(os.path.join(out, 'mapped.stp')) == IFSelect_RetDone
reader.TransferRoots()
shape = reader.OneShape()
golden['mapped.stp'] = props(shape)
golden['mapped.stp']['instances'] = 5

json.dump(golden, open(golden_path, 'w'), indent=1)
open(golden_path, 'a').write('\n')
