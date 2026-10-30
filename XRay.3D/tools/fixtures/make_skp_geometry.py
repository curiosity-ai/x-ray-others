#!/usr/bin/env python3
"""Writes dataset/synthetic/geometry-2021.skp (CC0): a SketchUp 2021-shaped file whose model.dat holds a small scene in the record layout
described in SketchUpVff.cs: a 4x4 plate with a square hole, and a 2x2x2 cube component placed twice (a translation, and a quarter turn with scale 2),
one placement on the layer "Walls", the cube painted with the material "Oak".

    python3 tools/fixtures/make_skp_geometry.py dataset/synthetic
"""
import io, os, struct, sys, zipfile

out = sys.argv[1]
ns = {}
exec(compile(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'make_cfb_fixtures.py')).read().split('# FMTIDs as stored')[0], 'x', 'exec'), ns)
png = ns['png']


def header(version):
    name = 'SketchUp Model'
    v = '{%s}' % version
    return b'\xff\xfe\xff' + bytes([len(name)]) + name.encode('utf-16-le') + b'\xff\xfe\xff' + bytes([len(v)]) + v.encode('utf-16-le')


def rec(tag, payload=b''):
    return bytes.fromhex(tag) + struct.pack('<I', len(payload)) + payload


def idrec(n):
    return rec('DC05', rec('DE05', struct.pack('<I', n)))


def u32(n): return struct.pack('<I', n)
def f3(*v): return struct.pack('<3d', *v)


class Geo:
    def __init__(self):
        self.vertices = {}
        self.edges = {}
        self.records = []
        self.next = 1

    def vertex(self, p):
        if p not in self.vertices:
            self.vertices[p] = self.next; self.next += 1
            self.records.append(rec('C409', idrec(self.vertices[p]) + rec('C509', f3(*p))))
        return self.vertices[p]

    def edge(self, a, b):
        """Returns (edge id, forward) for the traversal a -> b."""
        if (a, b) in self.edges: return self.edges[(a, b)], True
        if (b, a) in self.edges: return self.edges[(b, a)], False
        va, vb = self.vertex(a), self.vertex(b)
        e = self.next; self.next += 1
        self.edges[(a, b)] = e
        self.records.append(rec('B80B', idrec(e) + rec('B90B', u32(va)) + rec('BA0B', u32(vb))))
        return e, True

    def face(self, loops, normal, material=None):
        coloops = b''
        for loop in loops:
            cos = b''
            for i in range(len(loop)):
                e, fw = self.edge(loop[i], loop[(i + 1) % len(loop)])
                cos += rec('A00F', rec('A10F', u32(e)) + rec('A20F', u32(0 if fw else 1)))
            coloops += rec('9411', cos)
        fid = self.next; self.next += 1
        body = idrec(fid) + rec('AD0D', f3(*normal)) + rec('AE0D', coloops)
        if material is not None:
            body += rec('D007', rec('D107', u32(material)))
        self.records.append(rec('AC0D', body))

    def instance(self, definition, name, matrix, layer=None):
        body = rec('6819', bytes(range(16))) + rec('6719', u32(definition)) + rec('6519', name.encode()) + rec('6619', struct.pack('<13d', *matrix))
        if layer is not None:
            body += rec('D007', rec('D207', bytes([layer])))
        self.records.append(rec('6419', body))


# the cube definition
cube = Geo()
c = [(0, 0, 0), (2, 0, 0), (2, 2, 0), (0, 2, 0), (0, 0, 2), (2, 0, 2), (2, 2, 2), (0, 2, 2)]
for quad, n in [((0, 3, 2, 1), (0, 0, -1)), ((4, 5, 6, 7), (0, 0, 1)), ((0, 1, 5, 4), (0, -1, 0)), ((2, 3, 7, 6), (0, 1, 0)), ((1, 2, 6, 5), (1, 0, 0)), ((3, 0, 4, 7), (-1, 0, 0))]:
    cube.face([[c[i] for i in quad]], n, material=7)
cube_def = rec('7C15', idrec(100) + rec('8813', b''.join(cube.records)) + rec('7D15', bytes(range(16))) + rec('7E15', b'Cube') + rec('8315', u32(0)))

# the model
root = Geo()
root.face([[(0, 0, 0), (4, 0, 0), (4, 4, 0), (0, 4, 0)], [(1, 1, 0), (1, 3, 0), (3, 3, 0), (3, 1, 0)]], (0, 0, 1))
root.instance(100, 'Cube A', [1, 0, 0, 0, 1, 0, 0, 0, 1, 10, 0, 0, 1])
root.instance(100, '', [0, -1, 0, 1, 0, 0, 0, 0, 1, 0, 10, 0, 2], layer=1)
model_dat = rec('F401',
    rec('6300', u32(0)) +
    rec('F801', rec('983A', rec('993A', rec('8C3C', idrec(1) + rec('8D3C', b'Walls') + rec('8E3C', b'\x00'))))) +
    rec('F701', rec('D430', rec('D530', rec('C832', idrec(7) + rec('CC32', b'Oak'))))) +
    rec('F901', rec('7017', rec('7117', cube_def))) +
    rec('F601', rec('8813', b''.join(root.records))))

mat = ('<?xml version="1.0" encoding="UTF-8" standalone="no" ?>\n<materialDocument xmlns="http://sketchup.google.com/schemas/sketchup/1.0/material" xmlns:mat="http://sketchup.google.com/schemas/sketchup/1.0/material">\n'
       '  <mat:material name="%s" type="1" workflow="0" colorRed="%d" colorGreen="%d" colorBlue="%d" colorizeType="0" trans="%s" useTrans="%d" hasTexture="%d" />\n</materialDocument>\n')
buf = io.BytesIO()
with zipfile.ZipFile(buf, 'w', zipfile.ZIP_DEFLATED) as z:
    for name, data in [('meta/model_thumbnail.png', png(20, 12, (200, 150, 90))), ('materials/Oak/material.xml', mat % ('Oak', 160, 110, 60, '0', 0, 0)),
                       ('materials/Layer_Walls/material.xml', mat % ('Layer_Walls', 255, 84, 84, '0', 0, 0)), ('model.dat', model_dat)]:
        zi = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0)); zi.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(zi, data)
open(os.path.join(out, 'geometry-2021.skp'), 'wb').write(header('21.1.299') + b'VFF\x08\x00\x01\x00\x0d' + buf.getvalue())
