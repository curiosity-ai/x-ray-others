"""Golden values for real Revit and Inventor files (not redistributable; read from XRAY_3D_LOCAL_SAMPLES), from an independent reader
(olefile, zipfile, zlib) that applies the same documented rules as CompoundDocumentReader.

    python3 make_cfb_real_golden.py <out.json> <file> [<file> ...]
"""
import hashlib, io, json, os, re, struct, sys, uuid, zipfile
import olefile

def utf16_runs(d, minlen):
    out = []
    for al in (0, 1):
        for m in re.finditer(rb'(?:[\x20-\x7e]\x00){%d,}' % minlen, d[al:]):
            out.append(m.group().decode('utf-16le'))
    return out

def png_info(blob):
    i = blob.find(b'\x89PNG\r\n\x1a\n')
    if i < 0: return None
    w, h = struct.unpack('>II', blob[i + 16:i + 24])
    return {'width': w, 'height': h, 'offset': i}

def revit(o):
    r = {}
    b = o.openstream('BasicFileInfo').read()
    best = {}
    for al in (0, 1):
        t = b[al:len(b) - ((len(b) - al) & 1)].decode('utf-16le', 'replace')
        t = ''.join(c if (c >= ' ' or c in '\r\n') else '\n' for c in t)
        found = {}
        for m in re.finditer(r'(Revit Build|Build|Format|Username|Worksharing|Central Model Path|Last Save Path|Locale when saved|Locale|Open Workset Default|Unique Document GUID|Author):[ \t]*([^\r\n]*)', t):
            found.setdefault(m.group(1).strip(), m.group(2).strip())
        if len(found) > len(best): best = found
    r['application'] = best.get('Revit Build') or ('Autodesk Revit %s (Build: %s)' % (best['Format'], best['Build']) if 'Format' in best and 'Build' in best else None)
    r['guid'] = best.get('Unique Document GUID')
    r['last_save_path'] = best.get('Last Save Path')
    z = zipfile.ZipFile(io.BytesIO(o.openstream('ProjectInformation').read()))
    x = z.read([n for n in z.namelist() if n.endswith('.xml')][0]).decode()
    r['title'] = re.search(r'<title>([^<]*)</title>', x).group(1)
    r['modified'] = re.search(r'<updated>([^<]*)</updated>', x).group(1)
    r['project'] = dict(re.findall(r'displayName="([^"]*)"[^>]*>([^<]*)<', x))
    td = o.openstream('TransmissionData').read()[4:].decode('utf-16le', 'replace')
    files = []
    for p in re.findall(r'<LastSavedPath>([^<]*)</LastSavedPath>', td):
        n = p.replace('\\', '/').split('/')[-1]
        if n and n not in files: files.append(n)
    r['links'] = files
    g = o.openstream('Global/Latest').read()
    i = g.find(b'\x1f\x8b\x08')
    import zlib
    raw = zlib.decompressobj(-15).decompress(g[i + 10:])
    r['history'] = sorted({s.rstrip('/') for s in utf16_runs(raw, 24) if s.startswith('Revit 20')})
    best_png = None
    for n in o.listdir(streams=True, storages=False):
        if n[-1].startswith('RevitPreview'):
            best_png = best_png or png_info(o.openstream('/'.join(n)).read())
    r['preview'] = best_png
    return r

def inventor(o):
    sets = {}
    for s in o.listdir():
        if s[-1].startswith('\x05'):
            d = o.openstream('/'.join(s)).read()
            for sid in range(struct.unpack('<I', d[24:28])[0]):
                fm = str(uuid.UUID(bytes_le=d[28 + 20 * sid:44 + 20 * sid])).lower()
                sets.setdefault(fm, (d, struct.unpack('<I', d[44 + 20 * sid:48 + 20 * sid])[0]))
    def props(fm):
        if fm not in sets: return {}, {}
        d, off = sets[fm]
        n, = struct.unpack('<I', d[off + 4:off + 8]); vals = {}; names = {}
        plist = [struct.unpack('<II', d[off + 8 + 8 * k:off + 16 + 8 * k]) for k in range(n)]
        for pid, po in plist:
            p = off + po; t, = struct.unpack('<H', d[p:p + 2])
            if pid == 0:
                c, = struct.unpack('<I', d[p:p + 4]); q = p + 4
                for _ in range(c):
                    i2, l = struct.unpack('<II', d[q:q + 8]); names[i2] = d[q + 8:q + 8 + 2 * l].decode('utf-16le').rstrip('\0'); q += 8 + 2 * l; q = (q + 3) & ~3
            elif t == 0x1f:
                l, = struct.unpack('<I', d[p + 4:p + 8]); vals[pid] = d[p + 8:p + 8 + 2 * l].decode('utf-16le').rstrip('\0')
            elif t == 0x47:
                l, = struct.unpack('<I', d[p + 4:p + 8]); vals[pid] = ('cf', d[p + 8:p + 8 + l])
        return vals, names
    r = {}
    design, _ = props('32853f0f-3444-11d1-9e93-0060b03c1ca6')
    r['design'] = {k: design[i] for k, i in [('part_number', 5), ('description', 29), ('material', 20), ('appearance', 72), ('designer', 41), ('subtype', 32), ('build', 67)] if i in design}
    custom, names = props('9929adb8-6407-413e-b3dc-cb9ad2f564b7')
    r['custom'] = {names[i]: v for i, v in custom.items() if i in names and i != 255}
    summ, _ = props('3d38de39-0588-4c14-bb37-18f4d5dd31c7')
    r['author'] = summ.get(4)
    r['preview'] = png_info(summ[17][1]) if 17 in summ else None
    if 'UFRxDoc' in ['/'.join(s) for s in o.listdir()]:
        files = []
        for t in utf16_runs(o.openstream('UFRxDoc').read(), 6):
            if re.match(r'^(?:[A-Za-z]:\\|\\\\|\.{0,2}\\|/).*\.(?:ipt|iam|ipn|idw|dwg|stp|step|sat|jt|prt|sldprt|sldasm)$', t, re.I) and 12 <= len(t) <= 520:
                n = t.replace('\\', '/').split('/')[-1]
                if n not in files: files.append(n)
        r['references'] = sorted(files)
    return r

out = {}
for path in sys.argv[2:]:
    o = olefile.OleFileIO(path)
    out[os.path.basename(path)] = revit(o) if path.lower().endswith(('.rvt', '.rfa', '.rte', '.rft')) else inventor(o)
json.dump(out, open(sys.argv[1], 'w'), indent=1, sort_keys=True)
