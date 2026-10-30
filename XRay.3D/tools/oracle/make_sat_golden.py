#!/usr/bin/env python3
"""Expected values for the ACIS tests from an independent reader: ezdxf's ACIS loader (MIT) for the polyhedra it can read.

    /tmp/ezvenv/bin/python tools/oracle/make_sat_golden.py dataset/synthetic dataset/sat > tests/XRay.ThreeD.Tests/Corpus/sat-golden.json

Per file: the number of bodies, faces and distinct vertices, the exact bounding box and the enclosed volume and surface area of the
polygons ezdxf extracts (faces are planar, so these are exact). Files ezdxf cannot parse are listed with the reason.
"""
import glob, json, os, sys
import ezdxf.acis.api as acis
import numpy as np


def stats(meshes):
    faces = vertices = 0
    volume = area = 0.0
    lo = np.array([1e300] * 3)
    hi = np.array([-1e300] * 3)
    for m in meshes:
        v = np.array(m.vertices)
        lo = np.minimum(lo, v.min(axis=0))
        hi = np.maximum(hi, v.max(axis=0))
        vertices += len(set(map(tuple, np.round(v, 9))))
        for f in m.faces:
            p = v[list(f)]
            faces += 1
            for i in range(1, len(p) - 1):
                a, b, c = p[0], p[i], p[i + 1]
                volume += np.dot(a, np.cross(b, c)) / 6.0
                area += np.linalg.norm(np.cross(b - a, c - a)) / 2.0
    return {'faces': faces, 'vertices': vertices, 'volume': round(float(volume), 6), 'area': round(float(area), 6),
            'min': [round(float(x), 6) for x in lo], 'max': [round(float(x), 6) for x in hi]}


out = {}
for path in sorted(p for d in sys.argv[1:] for ext in ('sat', 'sab') for p in glob.glob(os.path.join(d, '*.' + ext))):
    try:
        data = open(path, 'rb').read()
        bodies = acis.load(data if path.endswith('.sab') else data.decode('latin-1').splitlines())
        meshes = acis.mesh_from_body(bodies[0]) if len(bodies) == 1 else [m for b in bodies for m in acis.mesh_from_body(b)]
        if not isinstance(meshes, list):
            meshes = [meshes]
        entry = {'bodies': len(bodies)}
        entry.update(stats(meshes))
        out[os.path.basename(path)] = entry
    except Exception as ex:  # the corpus files of other producers are outside ezdxf's profile
        out[os.path.basename(path)] = {'unsupported': type(ex).__name__ + ': ' + str(ex)[:80]}
print(json.dumps(out, indent=1))
