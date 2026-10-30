#!/usr/bin/env python3
"""Expected values for the DWG release tests (AC1015 to AC1032): the DXF twin of each sample drawing read with ezdxf (MIT).

    python3 tools/oracle/make_dwg_releases_golden.py <folder with example_2000.dwg/.dxf ...> > tests/XRay.ThreeD.Tests/Corpus/dwg-releases-golden.json

Per release: $INSUNITS, the layer names, the number of entities of each type (model space and blocks together, in the DXF twin) and the sums of the
coordinates of its LINE, POINT and CIRCLE entities, which a decoder that is wrong in one bit will not reproduce.
"""
import collections, json, os, sys
import ezdxf
from ezdxf.entities import DXFGraphic

folder = sys.argv[1]
out = {}
for release in ('2000', '2004', '2010', '2013', '2018'):
    doc = ezdxf.readfile(os.path.join(folder, f'example_{release}.dxf'))
    counts = collections.Counter()
    line_sum = point_sum = circle_sum = 0.0
    for e in doc.entitydb.values():
        if not isinstance(e, DXFGraphic):
            continue
        t = e.dxftype()
        counts[t] += 1
        if t == 'LINE':
            line_sum += sum(e.dxf.start) + sum(e.dxf.end)
        elif t == 'POINT':
            point_sum += sum(e.dxf.location)
        elif t == 'CIRCLE':
            circle_sum += sum(e.dxf.center) + e.dxf.radius
    out[release] = {
        'insunits': doc.header.get('$INSUNITS'),
        'layers': [l.dxf.name for l in doc.layers],
        'counts': dict(sorted(counts.items())),
        'lineSum': round(line_sum, 3), 'pointSum': round(point_sum, 3), 'circleSum': round(circle_sum, 3),
    }
print(json.dumps(out, indent=1))
