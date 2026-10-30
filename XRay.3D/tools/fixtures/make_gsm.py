#!/usr/bin/env python3
"""Writes dataset/synthetic/gdl-solids.gsm (CC0): an Archicad library part in the container layout observed in .gsm files (header, HEAD directory, sections
with 16-byte block headers; none of any real file's content is reproduced). Its GDL scripts exercise what the interpreter reads: parameters, FOR, WHILE, IF
with ELSE, GOSUB, the transformation stack, BLOCK, SPHERE, CONE and a PRISM_ with arcs.

    python3 tools/fixtures/make_gsm.py dataset/synthetic
"""
import os, struct, sys, uuid, zlib

out = sys.argv[1]
ns = {}
exec(compile(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'make_cfb_fixtures.py')).read().split('# FMTIDs as stored')[0], 'x', 'exec'), ns)
png = ns['png']


def block(tag, payload, version=0x14):
    return tag.encode()[::-1] + b'WW' + struct.pack('<HII', version, len(payload) + 16, 0) + payload


def text(s):
    return b'\xef\xbb\xbf' + s.encode()


params = [('A', 1.0), ('B', 0.5), ('ZZYZX', 1.0), ('steps', 4.0), ('stepLen', 0.25)]
pard = bytearray(81)
for name, value in params:
    pard += struct.pack('<d', value) + name.encode().ljust(32, b'\0') + b'\0' * 32
pard += b'\0' * 72

master = '''DEFINE MATERIAL "Oak" 0,
	0.7, 0.5, 0.3,  0.6, 0.85, 0.15, 0.25,
	20.0, 0.0,  0.9, 0.9, 0.9,  0, 0, 0, 0
'''
script3d = '''! Steps, spheres, a cone and a prism with rounded corners.
RESOL 24
MATERIAL "Oak"
FOR i = 1 TO steps
	ADD (i - 1) * stepLen, 0, 0
	BLOCK stepLen, B, i * 0.1
	DEL 1
NEXT i

k = 0
WHILE k < 2 DO
	ADD 0, 1.5 + k, 0
	SPHERE 0.25
	DEL 1
	k = k + 1
ENDWHILE

GOSUB 100
IF steps > 100 THEN
	BLOCK 9, 9, 9
ELSE
	ADD 0, 4, 0
	ROTZ 90
	CONE 1, 0.3, 0.1
	DEL 2
ENDIF

ADD 2, 0, 0
PRISM_ 9, 0.2,
	0, 0, 15,
	0.5, 0, 15,
	0.5, 0.1, 900,
	0.6, 0.1, 3015,
	0.6, 0.4, 15,
	0.1, 0.4, 15,
	0.1, 0.3, 15,
	0, 0.3, 15,
	0, 0, 15
DEL 1
END

100:
	ADD 0, -1, 0
	BLOCK 0.1, 0.1, 0.1
	DEL 1
RETURN
'''
sections = [
    ('1DSC', text(master)), ('3DSC', text(script3d)), ('2DSC', text('PROJECT2 3, 270, 2\n')), ('PRSC', text('')), ('UISC', text('')),
    ('VLSC', text('')), ('PARD', bytes(pard)),
    ('GIFF', b'image/png\0' + png(8, 8, (160, 110, 60))),
    ('CPYR', struct.pack('<HH', 1, 1) + struct.pack('<I', 8) + 'Examples'.encode('utf-16-le') + struct.pack('<HI', 1, 3) + 'CC0'.encode('utf-16-le') + struct.pack('<HI', 1, 3) + '1.0'.encode('utf-16-le') + struct.pack('<HI', 1, 0)),
    ('KWRD', struct.pack('<HI', 1, 0)), ('CTXT', text('Test object')),
]
count = len(sections) + 1
directory_size = 16 * count
dir_off = 0x80
body = b''
entries = []
offset = dir_off + directory_size
blocks = []
for tag, payload in sections:
    b = block(tag, payload)
    entries.append((tag, offset, len(b)))
    blocks.append(b)
    offset += len(b)
head = bytearray(0x80)
head[0:3] = b'WW-'
struct.pack_into('<I', head, 0x24, count)
head[0x28:0x30] = b'MYSGCASG'
head[0x30:0x34] = b'DAEH'
struct.pack_into('<II', head, 0x34, dir_off, directory_size)
head[0x48:0x58] = uuid.UUID('6c1d8e52-3b6a-4f0e-9d27-5a8e0b7c4d11').bytes_le
head[0x58:0x68] = uuid.UUID('b9f0a3c4-71d2-4e85-a6b3-0c2d9e1f7a48').bytes_le
struct.pack_into('<I', head, 0x68, 1)
directory = b''.join(tag.encode()[::-1] + struct.pack('<III', off, size, 0) for tag, off, size in [('HEAD', dir_off, directory_size)] + entries)
open(os.path.join(out, 'gdl-solids.gsm'), 'wb').write(bytes(head) + directory + b''.join(blocks))
