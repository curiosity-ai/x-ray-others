#!/usr/bin/env python3
"""Writes dataset/synthetic/xz/*.xz (CC0) and tests/XRay.ThreeD.Tests/Corpus/xz-golden.json with the sha256 and length of what each decodes to
(the python lzma module, liblzma, is the independent encoder the decoder is checked against).

    python3 -I tools/fixtures/make_xz.py dataset/synthetic/xz tests/XRay.ThreeD.Tests/Corpus/xz-golden.json
"""
import hashlib, json, lzma, os, random, struct, sys

out, golden = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)

def words(seed, n):
    r = random.Random(seed)
    vocab = [''.join(r.choice('abcdefghijklmnopqrstuvwxyz') for _ in range(r.randint(2, 9))) for _ in range(300)]
    return ' '.join(r.choice(vocab) if r.random() < .9 else str(r.randint(0, 99999)) for _ in range(n)).encode()

def floats(seed, n):
    r = random.Random(seed)
    return b''.join(struct.pack('<fff', r.uniform(-1, 1), round(r.uniform(-1, 1), 2), r.choice([0.0, 1.0, 0.5])) for _ in range(n))

r = random.Random(7)
samples = {
    'tiny': (b'hello xz', {}),
    'text-p6': (words(1, 30000), dict(preset=6)),
    'text-p0': (words(2, 20000), dict(preset=0)),
    'floats-p9': (floats(3, 20000), dict(preset=9)),
    'random': (bytes(r.getrandbits(8) for _ in range(70000)), dict(preset=6)),
    'zeros': (bytes(300000), dict(preset=6)),
    'repeat': (b'abcabcabd' * 40000, dict(preset=3)),
    'crc64': (words(4, 5000), dict(preset=6, check=lzma.CHECK_CRC64)),
    'nocheck': (words(5, 5000), dict(preset=6, check=lzma.CHECK_NONE)),
    'lc0lp2': (words(6, 8000), dict(filters=[{'id': lzma.FILTER_LZMA2, 'preset': 6, 'lc': 0, 'lp': 2, 'pb': 0}])),
}
gold = {}
for name, (data, kw) in sorted(samples.items()):
    kw = dict(kw)
    kw.setdefault('check', lzma.CHECK_CRC32)
    packed = lzma.compress(data, format=lzma.FORMAT_XZ, **kw)
    open(os.path.join(out, name + '.xz'), 'wb').write(packed)
    gold[name + '.xz'] = {'length': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
# two streams back to back with zero padding between them
a, b = words(8, 2000), words(9, 2000)
open(os.path.join(out, 'concat.xz'), 'wb').write(lzma.compress(a, format=lzma.FORMAT_XZ) + bytes(4) + lzma.compress(b, format=lzma.FORMAT_XZ))
gold['concat.xz'] = {'length': len(a) + len(b), 'sha256': hashlib.sha256(a + b).hexdigest()}
json.dump(gold, open(golden, 'w'), indent=1, sort_keys=True)
