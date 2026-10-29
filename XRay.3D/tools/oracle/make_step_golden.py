#!/usr/bin/env python3
"""Exact area, volume, bounding box and face counts of the STEP files, from OpenCASCADE (pip install cadquery-ocp).

    /tmp/venv/bin/python tools/oracle/make_step_golden.py dataset/step dataset/synthetic > tests/XRay.ThreeD.Tests/Corpus/step-golden.json

The tests compare the tessellation's surface area, enclosed volume and bounds with these within a tolerance that allows for the
chordal error of a tessellation. Bounds use the shape's own boundary (not the mesh), so they are exact.
"""
import collections, glob, json, os, sys
from OCP.STEPControl import STEPControl_Reader
from OCP.IFSelect import IFSelect_RetDone
from OCP.TopExp import TopExp_Explorer
from OCP.TopAbs import TopAbs_FACE, TopAbs_SOLID
from OCP.TopoDS import TopoDS
from OCP.BRepAdaptor import BRepAdaptor_Surface
from OCP.GeomAbs import GeomAbs_Plane, GeomAbs_Cylinder, GeomAbs_Cone, GeomAbs_Sphere, GeomAbs_Torus, GeomAbs_BSplineSurface, GeomAbs_SurfaceOfRevolution, GeomAbs_SurfaceOfExtrusion, GeomAbs_BezierSurface
from OCP.GProp import GProp_GProps
from OCP.BRepGProp import BRepGProp
from OCP.Bnd import Bnd_Box
from OCP.BRepBndLib import BRepBndLib
from OCP.BRepMesh import BRepMesh_IncrementalMesh

kinds = {GeomAbs_Plane: 'plane', GeomAbs_Cylinder: 'cylinder', GeomAbs_Cone: 'cone', GeomAbs_Sphere: 'sphere', GeomAbs_Torus: 'torus',
         GeomAbs_BSplineSurface: 'bspline', GeomAbs_SurfaceOfRevolution: 'revolution', GeomAbs_SurfaceOfExtrusion: 'extrusion', GeomAbs_BezierSurface: 'bezier'}
out = {}
for path in sorted(p for d in sys.argv[1:] for p in glob.glob(os.path.join(d, '*.st*p'))):
    reader = STEPControl_Reader()
    if reader.ReadFile(path) != IFSelect_RetDone:
        continue
    reader.TransferRoots()
    shape = reader.OneShape()
    counts = collections.Counter()
    explorer = TopExp_Explorer(shape, TopAbs_FACE)
    while explorer.More():
        face = TopoDS.Face(explorer.Current())
        counts[kinds.get(BRepAdaptor_Surface(face).GetType(), 'other')] += 1
        explorer.Next()
    solids = 0
    explorer = TopExp_Explorer(shape, TopAbs_SOLID)
    while explorer.More():
        solids += 1
        explorer.Next()
    area = GProp_GProps(); BRepGProp.SurfaceProperties_s(shape, area)
    volume = GProp_GProps(); BRepGProp.VolumeProperties_s(shape, volume)
    box = Bnd_Box(); BRepBndLib.AddOptimal_s(shape, box, False, False)
    lo, hi = box.CornerMin(), box.CornerMax()
    out[os.path.basename(path)] = {
        'faces': dict(counts), 'solids': solids,
        'area': round(area.Mass(), 4), 'volume': round(volume.Mass(), 4),
        'min': [round(lo.X(), 4), round(lo.Y(), 4), round(lo.Z(), 4)], 'max': [round(hi.X(), 4), round(hi.Y(), 4), round(hi.Z(), 4)],
    }
print(json.dumps(out, indent=1))
