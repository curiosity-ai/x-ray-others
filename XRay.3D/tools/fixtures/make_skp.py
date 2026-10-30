#!/usr/bin/env python3
"""Writes dataset/synthetic/model-2021.skp and model-2016.skp (CC0): SketchUp-shaped files (the header, version string and either the ZIP package behind the
prefix or a thumbnail PNG inside a serialised stream, as observed in public files); no real file's content is reproduced.

    python3 tools/fixtures/make_skp.py dataset/synthetic
"""
import io, os, random, struct, sys, zipfile

ns = {}
exec(compile(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'make_cfb_fixtures.py')).read().split('# FMTIDs as stored')[0], 'x', 'exec'), ns)
png = ns['png']
out = sys.argv[1]


def header(version):
    name = 'SketchUp Model'
    v = '{%s}' % version
    return b'\xff\xfe\xff' + bytes([len(name)]) + name.encode('utf-16-le') + b'\xff\xfe\xff' + bytes([len(v)]) + v.encode('utf-16-le')


rnd = random.Random(5)
mat = ('<?xml version="1.0" encoding="UTF-8" standalone="no" ?>\n<materialDocument xmlns="http://sketchup.google.com/schemas/sketchup/1.0/material" xmlns:mat="http://sketchup.google.com/schemas/sketchup/1.0/material">\n'
       '  <mat:material name="%s" type="1" workflow="0" colorRed="%d" colorGreen="%d" colorBlue="%d" colorizeType="0" trans="%s" useTrans="%d" hasTexture="%d" />\n</materialDocument>\n')
buf = io.BytesIO()
with zipfile.ZipFile(buf, 'w', zipfile.ZIP_DEFLATED) as z:
    z.writestr('meta/model_thumbnail.png', png(20, 12, (200, 150, 90)))
    z.writestr('meta/preview_thumbnail.png', png(20, 12, (200, 150, 90)))
    z.writestr('meta/meta.dat', bytes(range(40)))
    z.writestr('materials/Oak/material.xml', mat % ('Oak', 160, 110, 60, '0', 0, 1))
    z.writestr('materials/Oak/Oak.jpg', b'\xff\xd8\xff\xe0' + bytes(100))
    z.writestr('materials/Glass/material.xml', mat % ('Glass', 200, 220, 230, '0.6', 1, 0))
    z.writestr('materials/Layer_Walls/material.xml', mat % ('Layer_Walls', 255, 84, 84, '0', 0, 0))
    z.writestr('styles/[Simple Style]/style.xml', '<styleDocument/>')
    z.writestr('styles/[Simple Style]_1/style.xml', '<styleDocument/>')
    z.writestr('model.dat', bytes(rnd.randrange(256) for _ in range(3000)))
open(os.path.join(out, 'model-2021.skp'), 'wb').write(header('21.1.299') + b'VFF\x08\x00\x01\x00\x0d' + buf.getvalue())

classic = header('16.1.1449') + bytes(rnd.randrange(256) for _ in range(800)) + png(24, 18, (90, 130, 200)) + bytes(rnd.randrange(256) for _ in range(2000))
open(os.path.join(out, 'model-2016.skp'), 'wb').write(classic)
