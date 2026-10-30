#!/usr/bin/env python3
"""Writes dataset/synthetic/geometry-2017.skp (CC0): a SketchUp 2017-shaped file whose body is an MFC CArchive object stream in the layout described
in SketchUpLegacy.cs, holding the same scene as geometry-2021.skp in miniature: a 4x4 plate with a square hole as the one definition ("Plate",
a component), the model's own triangle painted with the material "Oak", and two placements of the plate (a translation, and a quarter turn with scale 2
named "Plate B"); two materials and one layer.

    python3 tools/fixtures/make_skp_legacy_geometry.py dataset/synthetic
"""
import os, struct, sys

out = sys.argv[1]


def header(version):
    name = 'SketchUp Model'
    v = '{%s}' % version
    return b'\xff\xfe\xff' + bytes([len(name)]) + name.encode('utf-16-le') + b'\xff\xfe\xff' + bytes([len(v)]) + v.encode('utf-16-le')


def u8(n): return bytes([n])
def u16(n): return struct.pack('<H', n)
def u32(n): return struct.pack('<I', n)
def f64(*v): return struct.pack('<%dd' % len(v), *v)
def s16(s): return b'\xff\xfe\xff' + bytes([len(s)]) + s.encode('utf-16-le')


class Ar:
    """Writes objects the way an MFC CArchive does: a new class takes a slot, every object takes the next one, later objects of a class
    carry 0x8000 | class slot and a repeated object is its slot number."""
    def __init__(self, first):
        self.next = first
        self.cls = {}
        self.buf = bytearray()

    def obj(self, name, schema, body):
        if name in self.cls:
            self.buf += u16(0x8000 | self.cls[name])
        else:
            self.cls[name] = self.next; self.next += 1
            self.buf += u16(0xFFFF) + u16(schema) + u16(len(name)) + name.encode()
        slot = self.next; self.next += 1
        body(slot)
        return slot

    def raw(self, b): self.buf += b


PRE = u16(0) + u8(0)       # null attribute container, empty persistent-id mask (version 17)
DB = lambda mat=0, hidden=0: u16(mat) + bytes([hidden, 0, 0, 0, 0, 0]) + u16(0)   # material, flag bytes (hidden, ..., soft, smooth, ...), layer 0


def vertex(ar, p):
    return ar.obj('CVertex', 1, lambda s: ar.raw(PRE + f64(*p)))


def edge_use(ar, state, a, b, loop_slot):
    """The coedge a -> b: the edge is written in full the first time (and the vertices with it)."""
    def body(_):
        ar.raw(PRE)
        def edge_body(_):
            ar.raw(PRE + DB())
            for p in (a, b):
                if p in state['v']: ar.raw(u16(state['v'][p]))
                else: state['v'][p] = vertex(ar, p)
            ar.raw(u16(0))
        ar.obj('CEdge', 1, edge_body)
        ar.raw(u8(0) + u16(loop_slot))
    ar.obj('CEdgeUse', 1, body)


def face(ar, state, loops, normal, mat=0):
    def body(_):
        ar.raw(PRE + DB(mat) + f64(*normal, 0.0) + u32(len(loops)))
        for pts in loops:
            def loop_body(slot, pts=pts):
                ar.raw(PRE + b'\x00\x00')
                for i in range(len(pts)):
                    edge_use(ar, state, pts[i], pts[(i + 1) % len(pts)], slot)
                ar.raw(u16(0))
            ar.obj('CLoop', 1, loop_body)
        ar.raw(u16(0))
    return ar.obj('CFace', 1, body)


def instance(ar, kind, def_slot, name, m, hidden=0):
    def body(_):
        ar.raw(PRE + DB(0, hidden) + u16(def_slot) + f64(*m) + s16(name) + bytes(16))
    return ar.obj(kind, 1, body)


def definition(ar, name, build):
    def body(_):
        ar.raw(PRE + bytes(22) + u32(0))            # id block, no layer list
        ar.raw(u16(0) + u32(0) + u32(1))            # filler, filler, entity count
        build()
        ar.raw(u32(0))                              # relationships
        ar.raw(u16(0) + bytes(range(16)) + s16(name) + s16('') + s16('') + u32(0))
        ar.raw(bytes(5) + u32(0))                   # behaviour bits, group flag 0 (component)
        def thumb(_):
            ar.raw(PRE)
            ar.obj('CCamera', 5, lambda s: ar.raw(bytes(137) + u16(0) + s16('') + bytes(33)))
            ar.obj('CDib', 3, lambda s: ar.raw(u32(0) + u32(0)))
        ar.obj('CThumbnail', 1, thumb)
    return ar.obj('CComponentDefinition', 2, body)


def material(ar, name, rgba, opacity=1.0, use=0):
    def body(_):
        ar.raw(PRE + s16(name) + u16(0) + bytes(rgba) + s16('') + bytes(8) + f64(opacity) + u8(use))
    return ar.obj('CMaterial', 1, body)


ar = Ar(3)
head = bytearray(header('17.0.18') + bytes(8))
head += u32(2)                                     # material count; the first CMaterial class record follows
oak = material(ar, 'Oak', [160, 110, 60, 255])
stone = material(ar, 'Stone', [128, 128, 130, 255], 0.5, 1)
ar.raw(u32(0) + u8(0) + u32(1))                      # a count, a flag, the layer count
layer = ar.obj('CLayer', 1, lambda s: ar.raw(PRE + s16('Walls') + b'\x00' + s16('') + u16(0) + bytes([255, 84, 84, 255]) + s16('') + bytes(21)))
ar.raw(u16(layer))                                  # the definition-list anchor, a back reference to a layer
ar.raw(u32(1))                                      # definitions

state = {'v': {}}
plate = definition(ar, 'Plate', lambda: face(ar, state, [[(0, 0, 0), (4, 0, 0), (4, 4, 0), (0, 4, 0)], [(1, 1, 0), (1, 3, 0), (3, 3, 0), (3, 1, 0)]], (0, 0, 1)))

ar.raw(u32(4))                                      # root entities
state = {'v': {}}
face(ar, state, [[(0, 0, 0), (2, 0, 0), (0, 2, 0)]], (0, 0, 1), oak)
instance(ar, 'CComponentInstance', plate, 'Plate A', [1, 0, 0, 0, 1, 0, 0, 0, 1, 10, 0, 0, 1])
instance(ar, 'CComponentInstance', plate, 'Plate B', [0, -1, 0, 1, 0, 0, 0, 0, 1, 0, 10, 0, 2])
instance(ar, 'CComponentInstance', plate, 'Hidden', [1, 0, 0, 0, 1, 0, 0, 0, 1, 50, 0, 0, 1], hidden=1)
open(os.path.join(out, 'geometry-2017.skp'), 'wb').write(bytes(head) + bytes(ar.buf))
