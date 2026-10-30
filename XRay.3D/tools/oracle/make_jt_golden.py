#!/usr/bin/env python3
"""Regenerates tests/XRay.ThreeD.Tests/Corpus/jt-golden.json from the JSON the JT Open reference toolkit wrote for five sample files (published with the
FerroJT repository, MIT, data/Reference/*.json): for the finest level of detail of every range LOD, the shape's index count, vertex count, normal count, the sum of
all vertex coordinates, the sum of all indices, the sum of all normal components and the diffuse colour.

    python3 -I tools/oracle/make_jt_golden.py <FerroJT>/data/Reference tests/XRay.ThreeD.Tests/Corpus/jt-golden.json
"""
import glob, json, os, sys

def collect(o, out, finest=True):
    if isinstance(o, dict):
        t = str(o.get('jt_type', ''))
        if 'Indices' in o and 'Vertices' in o and finest:
            v, ix, n = o['Vertices'], o['Indices'], o.get('Normals', [])
            out.add((len(ix), len(v) // 3, len(n) // 3, round(sum(v), 3), sum(ix), round(sum(n), 3), tuple(round(x, 4) for x in o.get('Attribute_MaterialDiffuseColor', []))))
        ch = o.get('children')
        if isinstance(ch, list):
            for i, c in enumerate(ch):
                collect(c, out, finest and ('Range' not in t or i == 0))
        for k, v in o.items():
            if k != 'children' and isinstance(v, dict):
                collect(v, out, finest)

gold = {}
for f in sorted(glob.glob(os.path.join(sys.argv[1], '*.json'))):
    d = json.load(open(f))
    out = set()
    collect(d['LSG'], out)
    gold[os.path.basename(f).replace('.json', '.jt')] = [list(x) for x in sorted(out)]
json.dump(gold, open(sys.argv[2], 'w'), indent=1)
