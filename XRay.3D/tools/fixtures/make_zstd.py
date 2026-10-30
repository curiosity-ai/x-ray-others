#!/usr/bin/env python3
"""Writes dataset/synthetic/zstd/*.zst (CC0) and tests/XRay.ThreeD.Tests/Corpus/zstd-golden.json with the sha256 and length of what each decodes to.

Needs the python `zstandard` package (the independent encoder the decoder is checked against).

    python3 -I tools/fixtures/make_zstd.py dataset/synthetic/zstd tests/XRay.ThreeD.Tests/Corpus/zstd-golden.json
"""
import hashlib, json, os, random, struct, sys
import zstandard as zstd

out, golden = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)

def words(seed, n):
    r = random.Random(seed)
    vocab = [''.join(r.choice('abcdefghijklmnopqrstuvwxyz') for _ in range(r.randint(2, 9))) for _ in range(400)]
    return ' '.join(r.choice(vocab) if r.random() < .9 else str(r.randint(0, 99999)) for _ in range(n)).encode()

def floats(seed, n):
    r = random.Random(seed)
    return b''.join(struct.pack('<fff', r.uniform(-1, 1), round(r.uniform(-1, 1), 2), r.choice([0.0, 1.0, 0.5])) for _ in range(n))

samples = {
    'tiny': (b'hello zstd', {}),
    'text-l1': (words(1, 40000), dict(level=1)),
    'text-l3': (words(1, 40000), dict(level=3)),
    'text-l9': (words(2, 40000), dict(level=9)),
    'text-l19': (words(3, 40000), dict(level=19)),
    'text-fast': (words(4, 40000), dict(level=-5)),
    'floats-l3': (floats(5, 12000), dict(level=3)),
    'floats-l19': (floats(6, 12000), dict(level=19)),
    'zeros': (bytes(300000), dict(level=3)),
    'runs': (b''.join(bytes([i % 7]) * (50 + i % 90) for i in range(3000)), dict(level=3)),
    'checksum': (words(7, 5000), dict(level=3, write_checksum=True)),
    'nosize': (words(8, 30000), dict(level=3, write_content_size=False)),
    'small-window': (words(9, 40000), dict(level=3, params=zstd.ZstdCompressionParameters.from_level(3, window_log=10))),
}
gold = {}
for name, (data, kw) in samples.items():
    if 'params' in kw:
        c = zstd.ZstdCompressor(compression_params=kw['params'])
    else:
        c = zstd.ZstdCompressor(**kw)
    if name == 'nosize':
        co = c.compressobj()
        blob = co.compress(data[:1000]) + co.compress(data[1000:]) + co.flush()
    else:
        blob = c.compress(data)
    open(os.path.join(out, name + '.zst'), 'wb').write(blob)
    gold[name + '.zst'] = {'length': len(data), 'sha256': hashlib.sha256(data).hexdigest()}

# Two frames with a skippable frame between them: the output is the concatenation.
a, b = words(10, 3000), floats(11, 2000)
blob = zstd.ZstdCompressor(level=3).compress(a) + b'\x50\x2a\x4d\x18' + struct.pack('<I', 5) + b'junk!' + zstd.ZstdCompressor(level=5).compress(b)
open(os.path.join(out, 'multi-frame.zst'), 'wb').write(blob)
gold['multi-frame.zst'] = {'length': len(a + b), 'sha256': hashlib.sha256(a + b).hexdigest()}
json.dump(gold, open(golden, 'w'), indent=1, sort_keys=True)
