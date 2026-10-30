#!/usr/bin/env python3
"""Writes dataset/synthetic/part.ipt (an Inventor-shaped compound file: summary information with a thumbnail) and model.rvt (a
Revit-shaped one: BasicFileInfo and a preview stream), each a valid OLE compound file (checked by reading it back with olefile), and
the expected property values, as olefile reads them, to tests/XRay.ThreeD.Tests/Corpus/cfb-golden.json.

    /tmp/venv/bin/python tools/fixtures/make_cfb_fixtures.py dataset/synthetic tests/XRay.ThreeD.Tests/Corpus/cfb-golden.json
"""
import json, os, struct, sys, zlib, datetime
import olefile

SECTOR, MINI, CUTOFF = 512, 64, 4096
FREE, EOC, FATSECT = 0xFFFFFFFF, 0xFFFFFFFE, 0xFFFFFFFD
NOSTREAM = 0xFFFFFFFF


def write_cfb(path, tree):
    """tree: {name: bytes | dict} (a dict is a storage). Entries are all black, siblings chained to the right."""
    entries = [{'name': 'Root Entry', 'type': 5, 'children': tree, 'data': b''}]

    def add(children):
        ids = []
        for name in sorted(children, key=lambda n: (len(n), n.upper())):
            v = children[name]
            e = {'name': name, 'type': 1 if isinstance(v, dict) else 2, 'data': b'' if isinstance(v, dict) else v, 'children': v if isinstance(v, dict) else {}}
            entries.append(e)
            e['id'] = len(entries) - 1
            ids.append(e)
        for e in ids:
            if e['type'] == 1:
                e['child'] = add(e['children'])
        for a, b in zip(ids, ids[1:]):
            a['right'] = b['id']
        return ids[0]['id'] if ids else NOSTREAM
    entries[0]['id'] = 0
    entries[0]['child'] = add(tree)
    mini_stream = bytearray()
    minifat = []
    big = []
    for e in entries[1:]:
        if e['type'] != 2:
            continue
        data = e['data']
        if len(data) < CUTOFF:
            e['start'] = len(mini_stream) // MINI if data else EOC
            n = (len(data) + MINI - 1) // MINI
            for i in range(n):
                minifat.append(e['start'] + i + 1 if i + 1 < n else EOC)
            mini_stream += data + b'\0' * (n * MINI - len(data))
        else:
            big.append(e)
    dir_sectors = (len(entries) * 128 + SECTOR - 1) // SECTOR
    minifat_sectors = (len(minifat) * 4 + SECTOR - 1) // SECTOR
    mini_sectors = (len(mini_stream) + SECTOR - 1) // SECTOR
    big_sectors = sum((len(e['data']) + SECTOR - 1) // SECTOR for e in big)
    others = dir_sectors + minifat_sectors + mini_sectors + big_sectors
    fat_sectors = 1
    while fat_sectors * 128 < others + fat_sectors:
        fat_sectors += 1
    assert fat_sectors <= 109
    fat = [FREE] * (fat_sectors * 128)
    pos = 0
    sectors = []

    def chain(count):
        nonlocal pos
        first = pos
        for i in range(count):
            fat[pos] = pos + 1 if i + 1 < count else EOC
            pos += 1
        return first
    fat_first = pos
    for i in range(fat_sectors):
        fat[pos] = FATSECT
        pos += 1
    dir_first = chain(dir_sectors)
    minifat_first = chain(minifat_sectors) if minifat_sectors else EOC
    mini_first = chain(mini_sectors) if mini_sectors else EOC
    blobs = []
    for e in big:
        n = (len(e['data']) + SECTOR - 1) // SECTOR
        e['start'] = chain(n)
    entries[0]['start'] = mini_first
    entries[0]['size'] = len(mini_stream)

    def entry_bytes(e):
        name = e['name'].encode('utf-16-le') + b'\0\0'
        b = bytearray(128)
        b[:len(name)] = name
        struct.pack_into('<H', b, 64, len(name))
        b[66] = e['type']
        b[67] = 1
        struct.pack_into('<III', b, 68, NOSTREAM, e.get('right', NOSTREAM), e.get('child', NOSTREAM))
        struct.pack_into('<I', b, 116, e.get('start', 0 if e['type'] != 2 else EOC) if e['type'] != 1 else 0)
        size = e.get('size', len(e['data']))
        struct.pack_into('<Q', b, 120, size)
        return bytes(b)
    dir_bytes = b''.join(entry_bytes(e) for e in entries)
    dir_bytes += b'\0' * (dir_sectors * SECTOR - len(dir_bytes))
    # unused directory slots must read as free entries
    header = bytearray(512)
    header[:8] = bytes.fromhex('D0CF11E0A1B11AE1')
    struct.pack_into('<HHHHH', header, 0x18, 0x3E, 3, 0xFFFE, 9, 6)
    struct.pack_into('<I', header, 0x2C, fat_sectors)
    struct.pack_into('<I', header, 0x30, dir_first)
    struct.pack_into('<I', header, 0x38, CUTOFF)
    struct.pack_into('<I', header, 0x3C, minifat_first)
    struct.pack_into('<I', header, 0x40, minifat_sectors)
    struct.pack_into('<I', header, 0x44, EOC)
    struct.pack_into('<I', header, 0x48, 0)
    for i in range(109):
        struct.pack_into('<I', header, 0x4C + 4 * i, fat_first + i if i < fat_sectors else FREE)
    out = bytearray(header)
    out += b''.join(struct.pack('<I', v) for v in fat)
    out += dir_bytes
    mf = b''.join(struct.pack('<I', v) for v in minifat)
    out += mf + b'\xff' * (minifat_sectors * SECTOR - len(mf))
    out += bytes(mini_stream) + b'\0' * (mini_sectors * SECTOR - len(mini_stream))
    for e in big:
        out += e['data'] + b'\0' * (-len(e['data']) % SECTOR)
    open(path, 'wb').write(out)


def png(w, h, rgb):
    def chunk(t, d):
        c = struct.pack('>I', len(d)) + t + d
        return c + struct.pack('>I', zlib.crc32(t + d) & 0xFFFFFFFF)
    raw = b''.join(b'\0' + bytes(rgb) * w for _ in range(h))
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(raw)) + chunk(b'IEND', b'')


def filetime(dt):
    return int((dt - datetime.datetime(1601, 1, 1, tzinfo=datetime.timezone.utc)).total_seconds() * 10_000_000)


def property_set(props, fmtid):
    """props: list of (id, ('i2', v) | ('lpstr', s) | ('lpwstr', s) | ('filetime', dt) | ('cf', bytes))"""
    bodies = []
    for pid, (kind, v) in props:
        if kind == 'i2':
            b = struct.pack('<HHhH', 2, 0, v, 0)
        elif kind == 'lpstr':
            s = v.encode('cp1252') + b'\0'
            b = struct.pack('<HHI', 0x1E, 0, len(s)) + s
        elif kind == 'lpwstr':
            s = v.encode('utf-16-le') + b'\0\0'
            b = struct.pack('<HHI', 0x1F, 0, len(v) + 1) + s
        elif kind == 'filetime':
            b = struct.pack('<HHQ', 0x40, 0, filetime(v))
        elif kind == 'cf':
            b = struct.pack('<HHII', 0x47, 0, len(v) + 8, 0xFFFFFFFF) + struct.pack('<I', 2) + v
        else:
            raise ValueError(kind)
        b += b'\0' * (-len(b) % 4)
        bodies.append(b)
    head = 8 + 8 * len(props)
    table = b''
    off = head
    for (pid, _), b in zip(props, bodies):
        table += struct.pack('<II', pid, off)
        off += len(b)
    ps = struct.pack('<II', off, len(props)) + table + b''.join(bodies)
    return ps


def property_stream(fmtid, props):
    header = struct.pack('<HHI', 0xFFFE, 0, 0x00020006) + b'\0' * 16 + struct.pack('<I', 1)
    first = 28 + 20
    return header + bytes.fromhex(fmtid) + struct.pack('<I', first) + property_set(props, fmtid)


# FMTIDs as stored: little-endian GUID fields
SUMMARY = 'e0859ff2f94f6810ab9108002b27b3d9'
DOCSUMMARY = '02d5cdd59c2e1b10939708002b2cf9ae'

out, golden = sys.argv[1], sys.argv[2]
utc = datetime.timezone.utc
created = datetime.datetime(2025, 3, 14, 9, 26, 53, tzinfo=utc)
saved = datetime.datetime(2025, 11, 2, 16, 5, 0, tzinfo=utc)
thumb = png(8, 6, (200, 80, 40))
summary = property_stream(SUMMARY, [
    (1, ('i2', 1252)), (2, ('lpstr', 'Bracket, left')), (3, ('lpstr', 'Mounting bracket')), (4, ('lpstr', 'A. Draftsperson')),
    (5, ('lpstr', 'bracket; steel')), (6, ('lpstr', 'Rev B')), (8, ('lpstr', 'B. Reviewer')), (12, ('filetime', created)), (13, ('filetime', saved)),
    (17, ('cf', thumb)), (18, ('lpstr', 'Autodesk Inventor Professional 2025')),
])
docsummary = property_stream(DOCSUMMARY, [(1, ('i2', 1252)), (2, ('lpstr', 'Brackets')), (15, ('lpstr', 'Example Works'))])
big = bytes((i * 7) & 0xFF for i in range(9000))
write_cfb(os.path.join(out, 'part.ipt'), {'\x05SummaryInformation': summary, '\x05DocumentSummaryInformation': docsummary, 'RSeStorageData': big, 'Small': b'tiny stream'})

basic = ('Worksharing: Not enabled\r\nUsername: jdoe\r\nCentral Model Path: \r\nRevit Build: Autodesk Revit 2024 (Build: 20230308_1635(x64))\r\nLast Save Path: C:\\models\\house.rvt\r\nOpen Workset Default: 1\r\nProject Spark File: 0\r\nFormat: 2024\r\nLocale: ENU\r\n').encode('utf-16-le')
basic = b'\xff\xfe\xff\x0e' + struct.pack('<I', len(basic) // 2) + basic
preview = b'\x00' * 24 + thumb
write_cfb(os.path.join(out, 'model.rvt'), {'BasicFileInfo': basic, 'RevitPreview4.0': preview, 'Formats': {'Latest': b'x' * 5000}, 'Global': {'Latest': b'y' * 300}})

result = {}
for name in ('part.ipt', 'model.rvt'):
    f = olefile.OleFileIO(os.path.join(out, name))
    entry = {'streams': sorted('/'.join(s) for s in f.listdir())}
    if name == 'part.ipt':
        m = f.get_metadata()
        entry.update(title=m.title.decode(), subject=m.subject.decode(), author=m.author.decode(), keywords=m.keywords.decode(), comments=m.comments.decode(),
                     last_saved_by=m.last_saved_by.decode(), application=m.creating_application.decode(), created=m.create_time.isoformat() + 'Z', saved=m.last_saved_time.isoformat() + 'Z',
                     category=m.category.decode(), company=m.company.decode(), big_sum=sum(f.openstream('RSeStorageData').read()))
    else:
        entry['basic_len'] = len(f.openstream('BasicFileInfo').read())
        entry['formats_latest'] = len(f.openstream('Formats/Latest').read())
    result[name] = entry
json.dump(result, open(golden, 'w'), indent=1)
print(json.dumps(result)[:300])
