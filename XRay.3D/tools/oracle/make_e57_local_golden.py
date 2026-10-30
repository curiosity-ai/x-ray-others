#!/usr/bin/env python3
"""Golden values (point counts and coordinate sums in the scene frame, as pye57 converts them) of real E57 files kept outside the repository
under /tmp/claude-0/samples/e57 (the pye57 test data), written to tests/XRay.ThreeD.Tests/Corpus/e57-local-golden.json.

    /tmp/claude-0/ezvenv/bin/python tools/oracle/make_e57_local_golden.py /tmp/claude-0/samples/e57 tests/XRay.ThreeD.Tests/Corpus/e57-local-golden.json
"""
import glob, json, os, sys
import numpy as np
import pye57

src, out = sys.argv[1], sys.argv[2]
result = {}
for path in sorted(glob.glob(os.path.join(src, '*.e57'))):
    e = pye57.E57(path)
    scans = []
    for k in range(e.scan_count):
        data = e.read_scan(k, intensity=False, colors=False, ignore_missing_fields=True, transform=True)
        x = np.asarray(data['cartesianX']); y = np.asarray(data['cartesianY']); z = np.asarray(data['cartesianZ'])
        scans.append({'points': int(len(x)), 'sum': [float(x.sum()), float(y.sum()), float(z.sum())],
                      'min': [float(x.min()), float(y.min()), float(z.min())], 'max': [float(x.max()), float(y.max()), float(z.max())]})
    result[os.path.basename(path)] = scans
json.dump(result, open(out, 'w'), indent=1)
