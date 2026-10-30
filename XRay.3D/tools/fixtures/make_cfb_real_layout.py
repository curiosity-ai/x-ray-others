#!/usr/bin/env python3
"""Writes dataset/synthetic/inventor-hashed.iam and revit-2021.rvt (CC0): compound files laid out the way real Inventor 2021+ and Revit 2019+ files
are (observed on public files; none of their content is reproduced): Inventor keeps every property set in a stream named after a hash of its
format id, with a UTF-16 dictionary for user-defined properties, a thumbnail PNG behind an 8-byte metafile header, and the documents an
assembly refers to as UTF-16 paths in UFRxDoc; Revit puts length-prefixed strings in front of the BasicFileInfo text (leaving the text on an odd
byte offset), stores ProjectInformation as a ZIP holding an Atom document, TransmissionData as a length plus UTF-16 XML, and Global/Latest as an
8-byte prefix plus a gzip stream whose trailer is wrong.

    python3 tools/fixtures/make_cfb_real_layout.py dataset/synthetic
"""
import datetime, io, os, struct, sys, uuid, zipfile, zlib

src = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'make_cfb_fixtures.py')).read()
ns = {}
exec(compile(src[:src.index('# FMTIDs as stored')], 'make_cfb_fixtures.py', 'exec'), ns)
write_cfb, png, filetime = ns['write_cfb'], ns['png'], ns['filetime']
out = sys.argv[1]
utc = datetime.timezone.utc


def prop(kind, v):
    if kind == 'i2':
        b = struct.pack('<Hhh', 2, 0, v)[:2] + struct.pack('<H', 0) + struct.pack('<h', v) + b'\0\0'
    elif kind == 'i4':
        b = struct.pack('<HHi', 3, 0, v)
    elif kind == 'r8':
        b = struct.pack('<HHd', 5, 0, v)
    elif kind == 'lpwstr':
        s = v.encode('utf-16-le') + b'\0\0'
        b = struct.pack('<HHI', 0x1F, 0, len(v) + 1) + s
    elif kind == 'filetime':
        b = struct.pack('<HHQ', 0x40, 0, filetime(v))
    elif kind == 'cf':
        b = struct.pack('<HHII', 0x47, 0, len(v) + 8, 0xFFFFFFFF) + struct.pack('<I', 3) + v
    else:
        raise ValueError(kind)
    return b + b'\0' * (-len(b) % 4)


def pset(fmtid, props, names=None, title=None):
    """One property set in UTF-16 (code page 1200) with an optional dictionary (id -> name); property 255 is the set's name."""
    items = [(1, prop('i2', 1200))]
    if title:
        items.append((255, prop('lpwstr', title)))
    for pid, (kind, v) in props:
        items.append((pid, prop(kind, v)))
    if names:
        d = struct.pack('<I', len(names))
        for pid, n in names.items():
            d += struct.pack('<II', pid, len(n) + 1) + (n + '\0').encode('utf-16-le')
            d += b'\0' * (-len(d) % 4)
        items.insert(0, (0, d))
    head = 8 + 8 * len(items)
    table, body, off = b'', b'', head
    for pid, b in items:
        table += struct.pack('<II', pid, off)
        body += b
        off += len(b)
    block = struct.pack('<II', off, len(items)) + table + body
    stream = struct.pack('<HHI', 0xFFFE, 0, 0x00020006) + b'\0' * 16 + struct.pack('<I', 1) + uuid.UUID(fmtid).bytes_le + struct.pack('<I', 48) + block
    return stream


thumb = b'\x08\x00\xb4\x00\xb4\x00\x00\x00' + png(12, 12, (30, 120, 200))
t0 = datetime.datetime(2022, 6, 1, 8, 0, 0, tzinfo=utc)
t1 = datetime.datetime(2023, 2, 3, 17, 30, 0, tzinfo=utc)
streams = {
    '\x05Aaalpg0m0wzvuhc41dwauxbwJc': pset('8CF58000-DA66-4AE6-8FF0-7B58406FB049', [(15, ('lpwstr', 'Example Works'))], title='Inventor Document Summary Information'),
    '\x05Zrxrt4arFafyu34gYa3l3ohgHg': pset('3D38DE39-0588-4C14-BB37-18F4D5DD31C7', [(2, ('lpwstr', 'Pump housing')), (4, ('lpwstr', 'A. Designer')), (17, ('cf', thumb))], title='Inventor Summary Information'),
    '\x05PypkizqiUjudbposAayal4qdGf': pset('32853F0F-3444-11D1-9E93-0060B03C1CA6', [
        (4, ('filetime', t0)), (5, ('lpwstr', 'PH-100')), (20, ('lpwstr', 'Aluminum 6061')), (29, ('lpwstr', 'Pump housing, machined')),
        (32, ('lpwstr', 'Assembly')), (41, ('lpwstr', 'A. Designer')), (67, ('lpwstr', '2022.2 (Build 262287010, 287A)')), (72, ('lpwstr', 'Aluminum - Satin'))], title='Design Tracking Properties'),
    '\x05Qz4dgm1gRjudbpksAayal4qdGf': pset('D861FB30-3136-11D1-9E92-0060B03C1CA6', [(16, ('lpwstr', 'B. Reviewer')), (17, ('filetime', t1))], title='Design Tracking Control'),
    '\x05Ynltsm4aEtpcuzs1Lwgf30tmXf': pset('9929ADB8-6407-413E-B3DC-CB9AD2F564B7', [(3, ('lpwstr', '6.350 mm')), (4, ('lpwstr', '12.000 mm'))], names={3: 'plate_thickness', 4: 'hole_pitch'}, title='Inventor User Defined Properties'),
}
refs = ['C:\\work\\pump\\housing.ipt', 'C:\\work\\pump\\lid.ipt', 'C:\\work\\pump\\hardware\\M4_screw.ipt', 'Document N:\\Templates\\Standard (mm).iam was created using in-development software']
streams['UFRxDoc'] = b'\x0f\x00\x1b\x00' + b''.join(r.encode('utf-16-le') + b'\0\0\x01\x00' for r in refs)
streams['RSeStorage'] = {'RSeSegInfo': bytes(range(200)) * 30}
write_cfb(os.path.join(out, 'inventor-hashed.iam'), streams)

# Revit 2021 layout.
text = ('Worksharing: Not enabled\r\nUsername: \r\nCentral Model Path: \r\nFormat: 2021\r\nBuild: 20200220_1100(x64)\r\nLast Save Path: C:\\models\\annex.rvt\r\n'
        'Open Workset Default: 3\r\nUnique Document GUID: 0a1b2c3d-1111-2222-3333-444455556666\r\nLocale when saved: ENG\r\nAuthor: Autodesk Revit\r\n').encode('utf-16-le')
# Binary header: three length-prefixed UTF-16 strings, the last one an odd number of bytes long so the text that follows is misaligned.
header = struct.pack('<I', 14) + b'\0' * 14
for s in ('2021', '20200220_1100(x64)'):
    header += struct.pack('<I', len(s)) + s.encode('utf-16-le')
header += b'\x00'
basic = header + text
atom = ('<?xml version="1.0" encoding="UTF-8"?><entry xmlns="http://www.w3.org/2005/Atom" xmlns:A="urn:schemas-autodesk-com:partatom"><title>Annex</title>'
        '<updated>2023-04-05T06:07:08Z</updated><link rel="design-2d" type="application/rvt" href="."><A:design-file><A:title>Annex.rvt</A:title><A:product>Revit</A:product>'
        '<A:product-version>2021</A:product-version></A:design-file></link><A:features><A:feature><A:title>Project Information</A:title><A:group><A:title>Other</A:title>'
        '<Project_Name displayName="Project Name" type="system" typeOfParameter="Text">Annex</Project_Name><Client_Name displayName="Client Name" type="system" typeOfParameter="Text">Example Works</Client_Name>'
        '</A:group></A:feature></A:features></entry>')
buf = io.BytesIO()
with zipfile.ZipFile(buf, 'w', zipfile.ZIP_DEFLATED) as z:
    z.writestr('C:\\Temp\\Revit{1}.project.xml', atom)
tx = ('<?xml version="1.0"?>\r\n<TransmissionData isTransmitted="false" userData="" version="5">'
      '<ExternalFileReference><ElementId>1</ElementId><ExternalFileReferenceType>Keynote Table</ExternalFileReferenceType><LastSavedPath>Keynotes.txt</LastSavedPath></ExternalFileReference>'
      '<ExternalFileReference><ElementId>2</ElementId><ExternalFileReferenceType>Revit Link</ExternalFileReferenceType><LastSavedPath>..\\structure\\frame.rvt</LastSavedPath></ExternalFileReference>'
      '</TransmissionData>').encode('utf-16-le')
tx = struct.pack('<I', len(tx) // 2) + tx
history = '\x00'.join(['Revit 2020 2020 (2020.000) : 20190410_1515(x64)', 'filler', 'Revit 2021 2021 (2021.000) : 20200220_1100(x64)']).encode('utf-16-le')
co = zlib.compressobj(9, zlib.DEFLATED, -15)
deflated = co.compress(b'\x1c' + b'\0' * 30 + history + b'\0' * 50) + co.flush()
latest = b'\x05\0\0\0\0\0\0\0' + b'\x1f\x8b\x08\x00\x00\x00\x00\x00\x00\x0b' + deflated + b'\xde\xad\xbe\xef' + b'\0\0\0\0'
preview = b'b\x19"\x05' + b'\0' * 200 + png(16, 16, (90, 90, 90))
write_cfb(os.path.join(out, 'revit-2021.rvt'), {
    'BasicFileInfo': basic, 'ProjectInformation': buf.getvalue(), 'TransmissionData': tx, 'RevitPreview4.0': preview,
    'Global': {'Latest': latest}, 'Formats': {'Latest': b'x' * 5000}})
