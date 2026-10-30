"""Golden values for SolidWorks 2014-and-earlier files (OLE compound files with __ZLB streams), from olefile and zlib and a separate scan of the
display-list stream for face records (positions only). Not redistributable files: read from XRAY_3D_LOCAL_SAMPLES/solidworks-old.

    python3 make_sw_legacy_golden.py <out.json> <file> [<file> ...]
"""
import hashlib, json, os, re, struct, sys, zlib
import olefile

SIG = bytes.fromhex('231dd571da8148a2a85898b21b89ef99')

def unpack(d):
    if len(d) == 24 or d[:16] != SIG:
        return b''
    p, out = 16, b''
    while p + 8 <= len(d):
        u, c = struct.unpack('<II', d[p:p + 8]); p += 8
        if u == 0 and c == 0: break
        r = zlib.decompress(d[p:p + c]); assert len(r) == u; out += r; p += c
    return out

def faces(dl):
    S = bytes([4, 0, 0, 0, 8, 0, 0, 0, 2, 0, 0, 0]); F = bytes([12, 0, 0, 0, 100, 0, 0, 0, 2, 0, 0, 0])
    res = []; i = 0
    while True:
        i = dl.find(S, i)
        if i < 0: break
        n, = struct.unpack('<I', dl[i + 12:i + 16])
        q = i + 16 + 4 * n
        if n == 0 or n > 100000 or q + 16 > len(dl): i += 1; continue
        lens = struct.unpack('<%dI' % n, dl[i + 16:q])
        if dl[q:q + 12] == F:
            k, = struct.unpack('<I', dl[q + 12:q + 16])
            if k == sum(lens) and q + 16 + 24 * k + 16 <= len(dl) and dl[q + 16 + 12 * k:q + 28 + 12 * k] == F:
                pos = struct.unpack('<%df' % (3 * k), dl[q + 16:q + 16 + 12 * k])
                res.append((k, lens, pos)); i = q + 16 + 24 * k + 16; continue
        i += 1
    return res

out = {}
for path in sys.argv[2:]:
    o = olefile.OleFileIO(path)
    names = ['/'.join(s) for s in o.listdir()]
    e = {'streams': len(names)}
    dl = unpack(o.openstream('Contents/DisplayLists__ZLB').read()) if 'Contents/DisplayLists__ZLB' in names else b''
    fs = faces(dl)
    e['display_list_bytes'] = len(dl)
    e['display_list_sha256'] = hashlib.sha256(dl).hexdigest()
    e['faces'] = len(fs)
    e['vertices'] = sum(f[0] for f in fs)
    if fs:
        xs = [v for f in fs for v in f[2][0::3]]; ys = [v for f in fs for v in f[2][1::3]]; zs = [v for f in fs for v in f[2][2::3]]
        e['min'] = [min(xs), min(ys), min(zs)]; e['max'] = [max(xs), max(ys), max(zs)]
    kw = 'swXmlContents/KeyWords__ZLB'
    if kw in names:
        x = unpack(o.openstream(kw).read()).decode('utf-8', 'replace')
        m = re.search(r'<Keywords[^>]* Name="([^"]*)"', x); e['keywords_name'] = m.group(1) if m else None
        e['configurations'] = re.findall(r'<Configuration [^>]*Name="([^"]*)"', x)
    ct = 'swXmlContents/COMPINSTANCETREE__ZLB'
    if ct in names:
        x = unpack(o.openstream(ct).read()).decode('utf-8', 'replace')
        files = []
        for p in re.findall(r'<swFile [^>]*swPath="([^"]*)"', x)[1:]:
            n = p.replace('\\', '/').split('/')[-1]
            if n not in files: files.append(n)
        e['component_files'] = files
    out[os.path.basename(path)] = e
json.dump(out, open(sys.argv[1], 'w'), indent=1, sort_keys=True)
