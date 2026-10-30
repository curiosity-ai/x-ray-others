#!/usr/bin/env python3
"""Expected values for the DWG (AC1015) entity tests: the DXF twin of each drawing read with ezdxf (MIT).

    /tmp/ezvenv/bin/python tools/oracle/make_dwg_golden.py dataset/dxf flange-drawing studio-floor-plan > tests/XRay.ThreeD.Tests/Corpus/dwg-golden.json

Per drawing, the model-space entities (and the graphics of dimensions, which a DWG draws in place) of the types the DWG reader decodes: LINE (end points), CIRCLE, ARC, LWPOLYLINE (vertices, bulges, closed
flag), TEXT (string, insertion point, height), each with its layer; and the counts of the types it does not.
"""
import collections, json, os, sys
import ezdxf

folder = sys.argv[1]
out = {}
for name in sys.argv[2:]:
    doc = ezdxf.readfile(os.path.join(folder, name + '.dxf'))
    entities = []
    other = collections.Counter()
    # A dimension draws the entities of its anonymous geometry block in place, so those count as model geometry.
    flat = []
    for e in doc.modelspace():
        if e.dxftype() == 'DIMENSION':
            flat.extend(doc.blocks.get(e.dxf.geometry))
            other['DIMENSION'] += 1
        else:
            flat.append(e)
    for e in flat:
        t = e.dxftype()
        d = {'type': t, 'layer': e.dxf.layer}
        if t == 'LINE':
            d['p'] = [round(v, 4) for v in tuple(e.dxf.start) + tuple(e.dxf.end)]
        elif t == 'CIRCLE':
            d['p'] = [round(v, 4) for v in tuple(e.dxf.center) + (e.dxf.radius,)]
        elif t == 'ARC':
            d['p'] = [round(v, 4) for v in tuple(e.dxf.center) + (e.dxf.radius, e.dxf.start_angle, e.dxf.end_angle)]
        elif t == 'LWPOLYLINE':
            pts = e.get_points('xyb')
            d['p'] = [[round(v, 4) for v in p] for p in pts]
            d['closed'] = bool(e.closed)
        elif t == 'TEXT':
            d['text'] = e.dxf.text
            d['p'] = [round(v, 4) for v in tuple(e.dxf.insert)] + [e.dxf.height]
            d['halign'] = e.dxf.get('halign', 0)
        else:
            other[t] += 1 if t != 'DIMENSION' else 0
            continue
        entities.append(d)
    out[name] = {'entities': entities, 'other': dict(other), 'layers': [l.dxf.name for l in doc.layers]}
print(json.dumps(out, indent=1))
