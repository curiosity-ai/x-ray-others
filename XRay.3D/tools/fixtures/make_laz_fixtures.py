#!/usr/bin/env python3
"""Writes dataset/synthetic/laz-f{0,1,2,3}.laz (point formats 0 to 3, the last with extra bytes; 1500 points in chunks of 400) with
laspy and its lazrs back end (independent LASzip implementation), and the SHA-256 of the uncompressed point records and a few
decoded values to tests/XRay.ThreeD.Tests/Corpus/laz-golden.json.

    /tmp/venv/bin/python tools/fixtures/make_laz_fixtures.py dataset/synthetic tests/XRay.ThreeD.Tests/Corpus/laz-golden.json
"""
import hashlib, json, os, random, sys
import numpy as np
import laspy, lazrs
import laspy._compression.lazrsbackend as backend

CHUNK = 400


class SmallChunkVlr:
    @staticmethod
    def new_for_compression(point_format_id, num_extra_bytes):
        record = bytearray(lazrs.LazVlr.new_for_compression(point_format_id, num_extra_bytes).record_data())
        record[12:16] = CHUNK.to_bytes(4, 'little')
        return lazrs.LazVlr(bytes(record))

    def __new__(cls, record_data):
        return lazrs.LazVlr(record_data)


backend.lazrs = type('L', (), {n: getattr(lazrs, n) for n in dir(lazrs) if not n.startswith('_')})
backend.lazrs.LazVlr = SmallChunkVlr

out, golden = sys.argv[1], sys.argv[2]
result = {}
for fmt in range(4):
    rnd = random.Random(100 + fmt)
    extra = 2 if fmt == 3 else 0
    header = laspy.LasHeader(point_format=fmt, version='1.2')
    header.scales = [0.001, 0.001, 0.001]
    header.offsets = [500000.0, 4000000.0, 100.0]
    if extra:
        header.add_extra_dim(laspy.ExtraBytesParams('ex', np.uint8, 'extra'))
        header.add_extra_dim(laspy.ExtraBytesParams('ey', np.uint8, 'extra'))
    n = 1500
    las = laspy.LasData(header)
    x = 500000.0; y = 4000000.0; z = 100.0
    xs, ys, zs = [], [], []
    for i in range(n):
        x += rnd.gauss(0, 0.8); y += rnd.gauss(0, 0.8); z += rnd.gauss(0, 0.3)
        xs.append(x); ys.append(y); zs.append(z)
    las.x = np.array(xs); las.y = np.array(ys); las.z = np.array(zs)
    las.intensity = np.array([rnd.choice([0, 100, rnd.randrange(65536), rnd.randrange(300)]) for _ in range(n)], dtype=np.uint16)
    nr = [rnd.randint(1, 5) for _ in range(n)]
    las.number_of_returns = np.array(nr, dtype=np.uint8)
    las.return_number = np.array([rnd.randint(1, k) for k in nr], dtype=np.uint8)
    las.scan_direction_flag = np.array([rnd.randint(0, 1) for _ in range(n)], dtype=np.uint8)
    las.edge_of_flight_line = np.array([int(rnd.random() < 0.05) for _ in range(n)], dtype=np.uint8)
    las.classification = np.array([rnd.choice([1, 2, 2, 2, 3, 5, 6, 9, 17]) for _ in range(n)], dtype=np.uint8)
    las.scan_angle_rank = np.array([max(-90, min(90, int(rnd.gauss(0, 20)))) for _ in range(n)], dtype=np.int8)
    las.user_data = np.array([rnd.choice([0, 0, 0, 7, rnd.randrange(256)]) for _ in range(n)], dtype=np.uint8)
    las.point_source_id = np.array([rnd.choice([1, 1, 1, 2, rnd.randrange(65536)]) for _ in range(n)], dtype=np.uint16)
    if fmt in (1, 3):
        t = 1.5e5
        times = []
        for i in range(n):
            r = rnd.random()
            t += rnd.uniform(0.0001, 0.001) if r < 0.9 else (rnd.uniform(0.5, 50) if r < 0.97 else -rnd.uniform(0.1, 3))
            times.append(t)
        las.gps_time = np.array(times)
    if fmt in (2, 3):
        base = [rnd.randrange(65536) for _ in range(3)]
        cols = []
        for i in range(n):
            if rnd.random() < 0.3:
                base = [rnd.randrange(65536) for _ in range(3)]
            elif rnd.random() < 0.5:
                base = [min(65535, max(0, c + rnd.randint(-300, 300))) for c in base]
            cols.append(base)
        cols = np.array(cols, dtype=np.uint16)
        las.red, las.green, las.blue = cols[:, 0], cols[:, 1], cols[:, 2]
    if extra:
        las.ex = np.array([rnd.randrange(256) for _ in range(n)], dtype=np.uint8)
        las.ey = np.array([i % 251 for i in range(n)], dtype=np.uint8)
    path = os.path.join(out, 'laz-f%d.laz' % fmt)
    las.write(path, laz_backend=laspy.LazBackend.Lazrs)
    back = laspy.read(path, laz_backend=laspy.LazBackend.Lazrs)
    raw = back.points.array.tobytes(); open('/tmp/claude-0/raw-f%d.bin' % fmt, 'wb').write(raw)
    sx, sy, sz = back.x, back.y, back.z
    result['laz-f%d.laz' % fmt] = {
        'points': n, 'recordLength': back.header.point_format.size, 'recordsSha256': hashlib.sha256(raw).hexdigest(),
        'x': [round(float(v), 3) for v in sx[[0, 1, 399, 400, 401, 799, 800, 1499]]],
        'y': [round(float(v), 3) for v in sy[[0, 1, 399, 400, 401, 799, 800, 1499]]],
        'z': [round(float(v), 3) for v in sz[[0, 1, 399, 400, 401, 799, 800, 1499]]],
        'rgb': [[int(back.red[i]), int(back.green[i]), int(back.blue[i])] for i in (0, 1, 400, 1499)] if fmt in (2, 3) else None,
    }
json.dump(result, open(golden, 'w'), indent=1)
print({k: v['recordsSha256'][:8] for k, v in result.items()})
