#!/usr/bin/env python3
"""Writes ACIS fixtures into dataset/synthetic.

Polyhedra come from ezdxf's ACIS exporter (MIT): text save version 700 and binary 700 (SAB). The cylinder, sphere and torus
are written here from the entity layout of the save format (version 400), with exactly known geometry: the tests check them against
closed-form area and volume.

    /tmp/ezvenv/bin/python tools/fixtures/make_sat_fixtures.py dataset/synthetic
"""
import math, os, sys
import ezdxf.acis.api as acis
from ezdxf.render import forms
from ezdxf.render.mesh import MeshTransformer

out = sys.argv[1]
os.makedirs(out, exist_ok=True)


def scaled(mesh, sx, sy, sz):
    mesh = MeshTransformer.from_builder(mesh)
    mesh.vertices = [(x * sx, y * sy, z * sz) for x, y, z in mesh.vertices]
    return mesh


box = scaled(forms.cube(center=False), 40, 20, 10)
prism = forms.cylinder(count=6, radius=5, top_radius=5, top_center=(0, 0, 8))
for name, mesh, version in (('box-700', box, 700), ('hexprism-700', prism, 700)):
    body = acis.body_from_mesh(mesh)
    open(os.path.join(out, name + '.sat'), 'w', newline='\n').write('\n'.join(acis.export_sat([body], version)) + '\n')
body = acis.body_from_mesh(box)
open(os.path.join(out, 'box-700.sab'), 'wb').write(acis.export_sab([body], 700))


class Sat:
    """A tiny writer for version 400 text: records are added with symbolic names and pointers resolved at the end."""

    def __init__(self):
        self.records = []

    def add(self, name, text):
        self.records.append((name, text))
        return name

    def render(self, product):
        index = {name: i for i, (name, _) in enumerate(self.records)}

        def resolve(text):
            parts = []
            for token in text.split(' '):
                parts.append('$%d' % index[token[1:]] if token.startswith('$') and token[1:] in index else token)
            return ' '.join(parts)

        lines = ['400 0 1 0 ', '%d %s 4 test 24 Mon Jan  1 00:00:00 2024 ' % (len(product), product), '1 9.9999999999999995e-007 1e-010 ']
        for _, text in self.records:
            lines.append(resolve(text) + ' #')
        lines.append('End-of-ACIS-data ')
        return '\n'.join(lines) + '\n'


def cylinder(r=2.0, h=5.0):
    s = Sat()
    s.add('body', 'body $-1 $lump $-1 $-1')
    s.add('lump', 'lump $-1 $-1 $shell $body')
    s.add('shell', 'shell $-1 $-1 $-1 $fbottom $-1 $lump')
    # faces
    # The bottom plane's own normal is -z (outward) and its loop runs the circle backwards, so the face takes the surface's side: 'forward'.
    s.add('fbottom', 'face $-1 $fside $lbottom $shell $-1 $sbottom forward single')
    s.add('fside', 'face $-1 $ftop $lside $shell $-1 $sside forward single')
    s.add('ftop', 'face $-1 $-1 $ltop $shell $-1 $stop forward single')
    s.add('lbottom', 'loop $-1 $-1 $c0 $fbottom')
    s.add('lside', 'loop $-1 $-1 $c2 $fside')
    s.add('ltop', 'loop $-1 $-1 $c1 $ftop')
    # coedges: next prev partner edge sense loop pcurve
    s.add('c0', 'coedge $-1 $c0 $c0 $c2 $e0 reversed $lbottom $-1')
    s.add('c1', 'coedge $-1 $c1 $c1 $c3 $e1 forward $ltop $-1')
    s.add('c2', 'coedge $-1 $c4 $c5 $c0 $e0 forward $lside $-1')
    s.add('c4', 'coedge $-1 $c3 $c2 $c5 $e2 forward $lside $-1')
    s.add('c3', 'coedge $-1 $c5 $c4 $c1 $e1 reversed $lside $-1')
    s.add('c5', 'coedge $-1 $c2 $c3 $c4 $e2 reversed $lside $-1')
    s.add('e0', 'edge $-1 $v0 $v0 $c2 $circle0 forward')
    s.add('e1', 'edge $-1 $v1 $v1 $c3 $circle1 forward')
    s.add('e2', 'edge $-1 $v0 $v1 $c4 $seam forward')
    s.add('v0', 'vertex $-1 $e0 $p0')
    s.add('v1', 'vertex $-1 $e1 $p1')
    s.add('p0', 'point $-1 %r 0 0' % r)
    s.add('p1', 'point $-1 %r 0 %r' % (r, h))
    s.add('circle0', 'ellipse-curve $-1 0 0 0 0 0 1 %r 0 0 1' % r)
    s.add('circle1', 'ellipse-curve $-1 0 0 %r 0 0 1 %r 0 0 1' % (h, r))
    s.add('seam', 'straight-curve $-1 %r 0 0 0 0 1' % r)
    s.add('sbottom', 'plane-surface $-1 0 0 0 0 0 -1 1 0 0 forward_v I I I I')
    s.add('stop', 'plane-surface $-1 0 0 %r 0 0 1 1 0 0 forward_v I I I I' % h)
    s.add('sside', 'cone-surface $-1 0 0 0 0 0 1 %r 0 0 1 I I 0 1 %r forward I I I I' % (r, r))
    return s.render('11 Test Writer')


open(os.path.join(out, 'cylinder-400.sat'), 'w', newline='\n').write(cylinder())


def single_face(surface_record):
    s = Sat()
    s.add('body', 'body $-1 $lump $-1 $-1')
    s.add('lump', 'lump $-1 $-1 $shell $body')
    s.add('shell', 'shell $-1 $-1 $-1 $face $-1 $lump')
    s.add('face', 'face $-1 $-1 $-1 $shell $-1 $surface forward single')
    s.add('surface', surface_record)
    return s.render('11 Test Writer')


open(os.path.join(out, 'sphere-400.sat'), 'w', newline='\n').write(single_face('sphere-surface $-1 1 2 3 3 1 0 0 0 0 1 forward_v I I I I'))
open(os.path.join(out, 'torus-400.sat'), 'w', newline='\n').write(single_face('torus-surface $-1 0 0 0 0 0 1 5 1 1 0 0 forward_v I I I I'))
