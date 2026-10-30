#!/usr/bin/env python3
"""Expected values for the DXF tests from an independent reader (ezdxf, MIT).

    python3 -m venv /tmp/ezvenv && /tmp/ezvenv/bin/pip install ezdxf==1.4.2
    /tmp/ezvenv/bin/python tools/oracle/make_dxf_golden.py dataset/dxf dataset/synthetic > tests/XRay.ThreeD.Tests/Corpus/dxf-golden.json

Per file: layer names, model-space entity counts by type, and the extents of model space. ezdxf's extents include
estimated text boxes, so the tests treat them as an outer bound for the drawn geometry, and check them for equality
only where a file has no text.
"""
import glob, json, os, sys, collections
import ezdxf
from ezdxf import bbox

out = {}
for path in sorted(p for d in sys.argv[1:] for p in glob.glob(os.path.join(d, '*.dxf'))):
    doc = ezdxf.readfile(path)
    msp = doc.modelspace()
    box = bbox.extents(msp)
    text_free = not any(e.dxftype() in ('TEXT', 'MTEXT', 'ATTRIB') for e in msp) and not any(
        e.dxftype() in ('TEXT', 'MTEXT', 'ATTRIB', 'ATTDEF') for blk in doc.blocks for e in blk)
    out[os.path.basename(path)] = {
        'version': doc.dxfversion,
        'units': doc.header.get('$INSUNITS', 0),
        'layers': [l.dxf.name for l in doc.layers],
        'entities': dict(collections.Counter(e.dxftype() for e in msp)),
        'extents': {'min': [round(float(v), 4) for v in box.extmin], 'max': [round(float(v), 4) for v in box.extmax]} if box.has_data else None,
        'textFree': text_free,
    }
print(json.dumps(out, indent=1))
