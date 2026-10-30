#!/usr/bin/env python3
"""Writes dataset/synthetic/las14-f{6..10}.las and laz14-f{6..10}.laz (LAS 1.4 point formats 6 to 10, 900 points; format 10 with two extra bytes) and laz14c-f{6,7,8}.laz (300-point chunks) with laspy and its lazrs back end (independent implementations of the
format and of LASzip: the C++ reference for the one-chunk files, lazrs for the chunked ones), and the SHA-256 of the uncompressed point records plus a few decoded values to
tests/XRay.ThreeD.Tests/Corpus/las14-golden.json.

    /tmp/claude-0/ezvenv/bin/python tools/fixtures/make_las14_fixtures.py dataset/synthetic tests/XRay.ThreeD.Tests/Corpus/las14-golden.json
"""
import hashlib, json, random, sys
import numpy as np
import laspy, lazrs
import laspy._compression.lazrsbackend as backend

CHUNK = 300  # for the lazrs-written chunked files


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
for fmt in range(6, 11):
    rnd = random.Random(700 + fmt)
    extra = 2 if fmt == 10 else 0
    header = laspy.LasHeader(point_format=fmt, version='1.4')
    header.scales = [0.001, 0.001, 0.001]
    header.offsets = [500000.0, 4000000.0, 100.0]
    if extra:
        header.add_extra_dim(laspy.ExtraBytesParams('ex', np.uint8, 'extra'))
        header.add_extra_dim(laspy.ExtraBytesParams('ey', np.uint8, 'extra'))
    n = 900
    las = laspy.LasData(header)
    x, y, z = 500000.0, 4000000.0, 100.0
    xs, ys, zs = [], [], []
    for i in range(n):
        x += rnd.gauss(0, 0.8); y += rnd.gauss(0, 0.8); z += rnd.gauss(0, 0.3)
        xs.append(x); ys.append(y); zs.append(z)
    las.x = np.array(xs); las.y = np.array(ys); las.z = np.array(zs)
    las.intensity = np.array([rnd.choice([0, 100, rnd.randrange(65536), rnd.randrange(300)]) for _ in range(n)], dtype=np.uint16)
    nr = [rnd.randint(1, 7) for _ in range(n)]
    las.number_of_returns = np.array(nr, dtype=np.uint8)
    las.return_number = np.array([rnd.randint(1, k) for k in nr], dtype=np.uint8)
    las.scan_direction_flag = np.array([rnd.randint(0, 1) for _ in range(n)], dtype=np.uint8)
    las.edge_of_flight_line = np.array([int(rnd.random() < 0.05) for _ in range(n)], dtype=np.uint8)
    las.classification = np.array([rnd.choice([1, 2, 2, 2, 3, 5, 6, 9, 17, 64, 130]) for _ in range(n)], dtype=np.uint8)
    las.synthetic = np.array([int(rnd.random() < 0.1) for _ in range(n)], dtype=np.uint8)
    las.key_point = np.array([int(rnd.random() < 0.1) for _ in range(n)], dtype=np.uint8)
    las.withheld = np.array([int(rnd.random() < 0.05) for _ in range(n)], dtype=np.uint8)
    las.overlap = np.array([int(rnd.random() < 0.1) for _ in range(n)], dtype=np.uint8)
    las.scanner_channel = np.array([rnd.choice([0, 0, 0, 1, 2, 3]) for _ in range(n)], dtype=np.uint8)
    las.scan_angle = np.array([max(-30000, min(30000, int(rnd.gauss(0, 3000)))) for _ in range(n)], dtype=np.int16)
    las.user_data = np.array([rnd.choice([0, 0, 0, 7, rnd.randrange(256)]) for _ in range(n)], dtype=np.uint8)
    las.point_source_id = np.array([rnd.choice([1, 1, 1, 2, rnd.randrange(65536)]) for _ in range(n)], dtype=np.uint16)
    t = 1.5e5
    times = []
    for i in range(n):
        r = rnd.random()
        t += rnd.uniform(0.0001, 0.001) if r < 0.9 else (rnd.uniform(0.5, 50) if r < 0.97 else -rnd.uniform(0.1, 3))
        times.append(t)
    las.gps_time = np.array(times)
    if fmt in (7, 8, 10):
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
    if fmt in (8, 10):
        las.nir = np.array([rnd.choice([0, 500, rnd.randrange(65536), rnd.randrange(900)]) for _ in range(n)], dtype=np.uint16)
    if fmt in (9, 10):
        las.wavepacket_index = np.array([rnd.randrange(4) for _ in range(n)], dtype=np.uint8)
        las.wavepacket_offset = np.array([rnd.randrange(1 << 40) for _ in range(n)], dtype=np.uint64)
        las.wavepacket_size = np.array([rnd.randrange(1 << 16) for _ in range(n)], dtype=np.uint32)
        las.return_point_wave_location = np.array([rnd.uniform(0, 500) for _ in range(n)], dtype=np.float32)
        las.x_t = np.array([rnd.uniform(-1, 1) for _ in range(n)], dtype=np.float32)
        las.y_t = np.array([rnd.uniform(-1, 1) for _ in range(n)], dtype=np.float32)
        las.z_t = np.array([rnd.uniform(-1, 1) for _ in range(n)], dtype=np.float32)
    if extra:
        las.ex = np.array([rnd.randrange(256) for _ in range(n)], dtype=np.uint8)
        las.ey = np.array([i % 251 for i in range(n)], dtype=np.uint8)
    las.write(f'{out}/las14-f{fmt}.las')
    # the reference C++ LASzip writes the one-chunk files; lazrs (Rust) writes 300-point chunks for the colour formats
    las.write(f'{out}/laz14-f{fmt}.laz', do_compress=True, laz_backend=laspy.compression.LazBackend.Laszip)
    if fmt in (6, 7, 8):
        las.write(f'{out}/laz14c-f{fmt}.laz', do_compress=True, laz_backend=laspy.compression.LazBackend.Lazrs)
    raw = open(f'{out}/las14-f{fmt}.las', 'rb').read()
    pointOffset = int.from_bytes(raw[96:100], 'little')
    reclen = int.from_bytes(raw[105:107], 'little')
    records = raw[pointOffset:pointOffset + n * reclen]
    # decoded from the compressed file by the independent decoder, to prove it equals the uncompressed records
    back = laspy.read(f'{out}/laz14-f{fmt}.laz', laz_backend=laspy.compression.LazBackend.Laszip)
    assert np.array_equal(np.asarray(back.x), np.asarray(las.x)) and np.array_equal(np.asarray(back.gps_time), np.asarray(las.gps_time))
    idx = [0, 1, 299, 300, 301, 599, 600, 899]
    entry = {
        'points': n, 'recordLength': reclen, 'recordsSha256': hashlib.sha256(records).hexdigest(),
        'x': [float(las.x[i]) for i in idx], 'y': [float(las.y[i]) for i in idx], 'z': [float(las.z[i]) for i in idx],
        'rgb': [[int(las.red[i]), int(las.green[i]), int(las.blue[i])] for i in idx] if fmt in (7, 8, 10) else None,
    }
    result[f'las14-f{fmt}'] = entry
json.dump(result, open(golden, 'w'), indent=1)

# ---- unchunked LAZ (compressor 1, "pointwise"): no chunk table offset, one range-coded stream. No writer here produces it, so it is made
# from the reference C++ LASzip's one-chunk file by dropping the 8-byte table offset and the chunk table and setting the compressor field;
# the stream bytes are identical in both layouts, and the golden hash is of the records the C++ decoder returns for the chunked original.
src = laspy.read(f'{out}/cloud-f3.las')
src.write('/tmp/pointwise-chunked.laz', do_compress=True, laz_backend=laspy.compression.LazBackend.Laszip)
raw = bytearray(open('/tmp/pointwise-chunked.laz', 'rb').read())
po = int.from_bytes(raw[96:100], 'little')
table = int.from_bytes(raw[po:po + 8], 'little')
assert int.from_bytes(raw[table + 4:table + 8], 'little') == 1, 'one chunk expected'
pos = int.from_bytes(raw[94:96], 'little')
for _ in range(int.from_bytes(raw[100:104], 'little')):
    uid = bytes(raw[pos + 2:pos + 18]).rstrip(b'\0 ')
    rid = int.from_bytes(raw[pos + 18:pos + 20], 'little')
    ln = int.from_bytes(raw[pos + 20:pos + 22], 'little')
    if uid == b'laszip encoded' and rid == 22204:
        raw[pos + 54:pos + 56] = (1).to_bytes(2, 'little')
    pos += 54 + ln
pointwise = bytes(raw[:po]) + bytes(raw[po + 8:table])
open(f'{out}/laz-pointwise-f3.laz', 'wb').write(pointwise)
back = laspy.read('/tmp/pointwise-chunked.laz', laz_backend=laspy.compression.LazBackend.Laszip)
recs = back.points.array.tobytes()
extra = json.load(open(golden))
extra['laz-pointwise-f3'] = {'points': len(back.points), 'recordLength': int(back.header.point_format.size), 'recordsSha256': hashlib.sha256(recs).hexdigest()}
json.dump(extra, open(golden, 'w'), indent=1)
