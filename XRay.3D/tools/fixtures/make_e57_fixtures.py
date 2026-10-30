#!/usr/bin/env python3
"""Writes dataset/synthetic/e57-two-scans.e57 (two scans of 1200 points with colour and intensity, the second with a pose) with
pye57 (libE57Format, an independent implementation), and tests/XRay.ThreeD.Tests/Corpus/e57-golden.json with what pye57 reads back
(point counts, per-scan coordinate sums in the scan's own frame, a few points, the pose).

    /tmp/claude-0/ezvenv/bin/python tools/fixtures/make_e57_fixtures.py dataset/synthetic tests/XRay.ThreeD.Tests/Corpus/e57-golden.json
"""
import json, random, sys
import numpy as np
import pye57

out, golden = sys.argv[1], sys.argv[2]
rnd = random.Random(57)
path = f'{out}/e57-two-scans.e57'
e = pye57.E57(path, mode='w')
scans = []
for k in range(2):
    n = 1200
    x = np.array([rnd.uniform(-5, 5) + 100 * k for _ in range(n)])
    y = np.array([rnd.uniform(-5, 5) for _ in range(n)])
    z = np.array([rnd.uniform(0, 3) for _ in range(n)])
    r = np.array([rnd.randrange(256) for _ in range(n)], dtype=np.uint8)
    g = np.array([rnd.randrange(256) for _ in range(n)], dtype=np.uint8)
    b = np.array([rnd.randrange(256) for _ in range(n)], dtype=np.uint8)
    inten = np.array([rnd.uniform(0, 1) for _ in range(n)])
    data = {'cartesianX': x, 'cartesianY': y, 'cartesianZ': z, 'colorRed': r, 'colorGreen': g, 'colorBlue': b, 'intensity': inten}
    rotation = np.array([0.7071067811865476, 0, 0, 0.7071067811865476]) if k == 1 else None
    translation = np.array([10.0, 20.0, 30.0]) if k == 1 else None
    e.write_scan_raw(data, name=f'scan {k}', rotation=rotation, translation=translation)
    scans.append(data)
e.close()

e = pye57.E57(path)
result = {'scans': []}
for k in range(e.scan_count):
    raw = e.read_scan_raw(k)
    glob = e.read_scan(k, ignore_missing_fields=True, transform=True)
    header = e.get_header(k)
    result['scans'].append({
        'points': int(len(raw['cartesianX'])),
        'sumX': float(np.sum(raw['cartesianX'])), 'sumY': float(np.sum(raw['cartesianY'])), 'sumZ': float(np.sum(raw['cartesianZ'])),
        'globalSumX': float(np.sum(glob['cartesianX'])), 'globalSumY': float(np.sum(glob['cartesianY'])), 'globalSumZ': float(np.sum(glob['cartesianZ'])),
        'first': [float(raw['cartesianX'][0]), float(raw['cartesianY'][0]), float(raw['cartesianZ'][0])],
        'firstColor': [int(raw['colorRed'][0]), int(raw['colorGreen'][0]), int(raw['colorBlue'][0])],
        'firstIntensity': float(raw['intensity'][0]),
        'name': header.node['name'].value(),
    })
json.dump(result, open(golden, 'w'), indent=1)
