#!/usr/bin/env python3
"""Writes dataset/synthetic/legacy-cube.sldprt (CC0): a SolidWorks-2014-style part, an OLE compound file whose compressed streams carry the __ZLB
signature and zlib chunks (observed on public pre-2015 files; none of their content is reproduced). It holds a 20 mm cube as six four-vertex
strip faces in the display list, KeyWords, the summary, document properties and a preview PNG. A second chunk in the display list exercises the
chunked form, and an empty __ZLB stream the empty form.

    python3 tools/fixtures/make_sw_legacy.py dataset/synthetic
"""
import datetime, os, struct, sys, zlib

src = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'make_cfb_fixtures.py')).read()
ns = {}
exec(compile(src[:src.index('# FMTIDs as stored')], 'make_cfb_fixtures.py', 'exec'), ns)
write_cfb, png, filetime = ns['write_cfb'], ns['png'], ns['filetime']
out = sys.argv[1]
SIG = bytes.fromhex('231dd571da8148a2a85898b21b89ef99')


def zlb(data, chunk=None):
    """The __ZLB form: signature, then (inflated size, deflated size, zlib stream) chunks, then two zero words."""
    chunk = chunk or max(1, len(data))
    out_ = SIG
    for i in range(0, len(data), chunk):
        part = data[i:i + chunk]
        z = zlib.compress(part, 6)
        out_ += struct.pack('<II', len(part), len(z)) + z
    return out_ + struct.pack('<II', 0, 0)


S = bytes([4, 0, 0, 0, 8, 0, 0, 0, 2, 0, 0, 0])
F = bytes([12, 0, 0, 0, 100, 0, 0, 0, 2, 0, 0, 0])
h = 0.01
corners = [(-h, -h, -h), (h, -h, -h), (h, h, -h), (-h, h, -h), (-h, -h, h), (h, -h, h), (h, h, h), (-h, h, h)]
quads = [((0, 3, 1, 2), (0, 0, -1)), ((4, 5, 7, 6), (0, 0, 1)), ((0, 1, 4, 5), (0, -1, 0)), ((3, 7, 2, 6), (0, 1, 0)), ((0, 4, 3, 7), (-1, 0, 0)), ((1, 2, 5, 6), (1, 0, 0))]
dl = b'\x01\x00\x00\x00' * 8
for idx, nrm in quads:
    dl += S + struct.pack('<II', 1, 4)
    dl += F + struct.pack('<I', 4) + b''.join(struct.pack('<fff', *corners[i]) for i in idx)
    dl += F + struct.pack('<I', 4) + b''.join(struct.pack('<fff', *nrm) for _ in idx)
    dl += b'\x00' * 12

keywords = b'\x86<?xml version="1.0" encoding="UTF-8"?>\r\n<Keywords id="1392241035" Name="Cube20"><Configuration id="0" Name="Default" Type="ConfigurationManager" Material="Material &lt;not specified&gt;"/><Extrusion id="34" Name="Boss-Extrude1" Type="Boss-Extrude"><Dimension Name="D1">20</Dimension></Extrusion></Keywords>'


def ansi_set(fmtid_le, props, names, cp=1252):
    """A property set with ANSI strings and a dictionary (the way ISolidWorksInformation is written)."""
    def lpstr(v):
        s = v.encode('cp1252') + b'\0'
        b = struct.pack('<HHI', 0x1E, 0, len(s)) + s
        return b + b'\0' * (-len(b) % 4)
    items = [(1, struct.pack('<HHhH', 2, 0, cp - 65536 if cp > 32767 else cp, 0))]
    for pid, v in props:
        items.append((pid, lpstr(v)))
    d = struct.pack('<I', len(names))
    for pid, n in names.items():
        d += struct.pack('<II', pid, len(n) + 1) + n.encode('cp1252') + b'\0'
    items.insert(0, (0, d + b'\0' * (-len(d) % 4)))
    head = 8 + 8 * len(items)
    table, body, off = b'', b'', head
    for pid, b in items:
        table += struct.pack('<II', pid, off)
        body += b
        off += len(b)
    block = struct.pack('<II', off, len(items)) + table + body
    return struct.pack('<HHI', 0xFFFE, 0, 0x00020006) + b'\0' * 16 + struct.pack('<I', 1) + bytes.fromhex(fmtid_le) + struct.pack('<I', 48) + block


def summary(props):
    items = [(1, struct.pack('<HHhH', 2, 0, 1252, 0))]
    for pid, v in props:
        if isinstance(v, str):
            s = v.encode('cp1252') + b'\0'
            b = struct.pack('<HHI', 0x1E, 0, len(s)) + s
        else:
            b = struct.pack('<HHQ', 0x40, 0, filetime(v))
        items.append((pid, b + b'\0' * (-len(b) % 4)))
    head = 8 + 8 * len(items)
    table, body, off = b'', b'', head
    for pid, b in items:
        table += struct.pack('<II', pid, off)
        body += b
        off += len(b)
    block = struct.pack('<II', off, len(items)) + table + body
    return struct.pack('<HHI', 0xFFFE, 0, 0x00020006) + b'\0' * 16 + struct.pack('<I', 1) + bytes.fromhex('e0859ff2f94f6810ab9108002b27b3d9') + struct.pack('<I', 48) + block


utc = datetime.timezone.utc
sw_info = ansi_set('05d5cdd59c2e1b10939708002b2cf9ae', [(3, 'legacy-cube'), (7, 'Default'), (15, 'Drafter')],
                   {3: 'SW-File Name(File Name)', 7: 'SW-Configuration Name(Configuration Name)', 15: 'SW-Last Saved By(Last Saved By)'})
custom = ansi_set('05d5cdd59c2e1b10939708002b2cf9ae', [(2, 'LC-001')], {2: 'Part Number'})
tree = {
    '\x05SummaryInformation': summary([(2, 'Cube20'), (4, 'A. Drafter'), (8, 'Drafter'), (12, datetime.datetime(2013, 5, 6, 7, 8, 9, tzinfo=utc)), (13, datetime.datetime(2014, 2, 12, 16, 41, 57, tzinfo=utc))]),
    'ISolidWorksInformation': sw_info,
    'Config-0-Properties': custom,
    'Header2': b'\xff\xff\x01\x00\n\x00moHeader_c' + bytes(range(64)),
    'Contents': {'DisplayLists__ZLB': zlb(dl, chunk=len(dl) // 2 + 1), 'Config-0': b'cfg' * 200, 'User Units Table__ZLB': SIG + struct.pack('<II', 0, 0)},
    'swXmlContents': {'KeyWords__ZLB': zlb(keywords)},
    'PreviewPNG': png(16, 12, (180, 180, 190)),
    '_MO_VERSION_7000': {'History': b'\x01\x00'},
}
write_cfb(os.path.join(out, 'legacy-cube.sldprt'), tree)
