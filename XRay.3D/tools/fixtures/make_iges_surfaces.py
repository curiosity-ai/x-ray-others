#!/usr/bin/env python3
"""IGES surface fixtures written by OpenCASCADE's IGES writer, with the exact values OpenCASCADE's IGES reader gives for them
(pip install cadquery-ocp).

    python3 -I tools/fixtures/make_iges_surfaces.py dataset tests/XRay.ThreeD.Tests/Corpus/iges-golden.json

One plate (box, through hole, sphere and cone on top), four ways:
  iges-brep-quadrics.igs   B-rep mode: 186 solid, 514 shell, 510 faces, 508 loops, 504/502 edges and vertices, quadrics 190 192 194 196
  iges-trimmed-faces.igs   face mode: 144 trimmed surfaces over planes (108) and surfaces of revolution (120), with 142 curves and 102 composites
  iges-nurbs-brep.igs      the same solid converted to B-splines: 128 surfaces under 510 faces
  iges-nurbs-faces.igs     the same faces as 144 trimmed 128 surfaces with 142 curves in parameter space
The golden file also holds the reader's values for the two real files under dataset/iges/, so every kind of file is checked against an
independent implementation: area, volume (solids only) and bounds of what the file draws.
"""
import json, os, sys
from OCP.IGESControl import IGESControl_Writer, IGESControl_Reader
from OCP.IFSelect import IFSelect_RetDone
from OCP.BRepPrimAPI import BRepPrimAPI_MakeBox, BRepPrimAPI_MakeCylinder, BRepPrimAPI_MakeSphere, BRepPrimAPI_MakeCone
from OCP.BRepAlgoAPI import BRepAlgoAPI_Cut, BRepAlgoAPI_Fuse
from OCP.BRepBuilderAPI import BRepBuilderAPI_NurbsConvert
from OCP.gp import gp_Pnt, gp_Ax2, gp_Dir
from OCP.GProp import GProp_GProps
from OCP.BRepGProp import BRepGProp
from OCP.Bnd import Bnd_Box
from OCP.BRepBndLib import BRepBndLib

root, golden_path = sys.argv[1], sys.argv[2]

def plate():
    s = BRepPrimAPI_MakeBox(gp_Pnt(0, 0, 0), 20, 10, 6).Shape()
    s = BRepAlgoAPI_Cut(s, BRepPrimAPI_MakeCylinder(gp_Ax2(gp_Pnt(5, 5, -1), gp_Dir(0, 0, 1)), 2.0, 10).Shape()).Shape()
    s = BRepAlgoAPI_Fuse(s, BRepPrimAPI_MakeSphere(gp_Pnt(14, 5, 6), 3.0).Shape()).Shape()
    return BRepAlgoAPI_Fuse(s, BRepPrimAPI_MakeCone(gp_Ax2(gp_Pnt(9, 2, 6), gp_Dir(0, 0, 1)), 1.5, 0.5, 3.0).Shape()).Shape()

def write(shape, name, brep):
    writer = IGESControl_Writer('MM', 1 if brep else 0)
    writer.AddShape(shape)
    writer.ComputeModel()
    writer.Write(os.path.join(root, 'synthetic', name))

def measure(path, solid):
    reader = IGESControl_Reader()
    assert reader.ReadFile(path) == IFSelect_RetDone
    reader.TransferRoots()
    shape = reader.OneShape()
    area = GProp_GProps(); BRepGProp.SurfaceProperties_s(shape, area)
    box = Bnd_Box(); BRepBndLib.AddOptimal_s(shape, box, False, False)
    lo, hi = box.CornerMin(), box.CornerMax()
    out = {'area': round(area.Mass(), 3), 'min': [round(lo.X(), 3), round(lo.Y(), 3), round(lo.Z(), 3)], 'max': [round(hi.X(), 3), round(hi.Y(), 3), round(hi.Z(), 3)]}
    if solid:
        volume = GProp_GProps(); BRepGProp.VolumeProperties_s(shape, volume)
        out['volume'] = round(volume.Mass(), 3)
    return out

shape = plate()
nurbs = BRepBuilderAPI_NurbsConvert(shape, True).Shape()
golden = {}
for shp, name, brep in ((shape, 'iges-brep-quadrics.igs', True), (shape, 'iges-trimmed-faces.igs', False), (nurbs, 'iges-nurbs-brep.igs', True), (nurbs, 'iges-nurbs-faces.igs', False)):
    write(shp, name, brep)
    golden['synthetic/' + name] = measure(os.path.join(root, 'synthetic', name), brep)
for name in sorted(os.listdir(os.path.join(root, 'iges'))):
    golden['iges/' + name] = measure(os.path.join(root, 'iges', name), True)
with open(golden_path, 'w') as f:
    json.dump(golden, f, indent=1)
    f.write('\n')
