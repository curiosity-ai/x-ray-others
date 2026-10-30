#!/usr/bin/env python3
"""Expected drawn text and filled areas for the SVG tests, from an independent reader (ezdxf, MIT).

    /tmp/ezvenv/bin/python tools/oracle/make_dxf_drawing_golden.py dataset/dxf dataset/synthetic > tests/XRay.ThreeD.Tests/Corpus/dxf-drawing-golden.json

Per file: the TEXT, MTEXT and ATTRIB entities of model space itself (not inside blocks) that lie in the XY plane, with their content
(MTEXT format codes stripped by ezdxf), insertion point, height and rotation, and the number of SOLID entities and of solid-filled
hatches in model space.
"""
import glob, json, os, sys
import ezdxf

out = {}
for path in sorted(p for d in sys.argv[1:] for p in glob.glob(os.path.join(d, '*.dxf'))):
    doc = ezdxf.readfile(path)
    msp = doc.modelspace()
    texts, solids, hatches = [], 0, 0
    for e in msp:
        t = e.dxftype()
        if t in ('TEXT', 'ATTRIB'):
            if tuple(e.dxf.get('extrusion', (0, 0, 1))) != (0, 0, 1) or not e.dxf.text.strip():
                continue
            p = e.dxf.insert
            texts.append({'text': e.dxf.text, 'x': round(p.x, 4), 'y': round(p.y, 4), 'height': round(e.dxf.height, 4), 'rotation': round(e.dxf.get('rotation', 0), 4)})
        elif t == 'MTEXT':
            if tuple(e.dxf.get('extrusion', (0, 0, 1))) != (0, 0, 1) or not e.plain_text().strip():
                continue
            p = e.dxf.insert
            texts.append({'text': e.plain_text(), 'x': round(p.x, 4), 'y': round(p.y, 4), 'height': round(e.dxf.get('char_height', 1), 4), 'rotation': round(e.dxf.get('rotation', 0), 4)})
        elif t == 'SOLID':
            solids += 1
        elif t == 'HATCH' and e.dxf.solid_fill == 1:
            hatches += 1
    out[os.path.basename(path)] = {'texts': texts, 'solids': solids, 'solidHatches': hatches}
print(json.dumps(out, indent=1))
