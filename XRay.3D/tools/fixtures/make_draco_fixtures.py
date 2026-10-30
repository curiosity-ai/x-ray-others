#!/usr/bin/env python3
"""Writes the Draco fixtures of dataset/draco/ (original work, CC0): small meshes encoded by an independent encoder (DracoPy, pip install DracoPy
numpy) in several ways, each with a JSON file holding what DracoPy itself decodes from it. The C# tests decode the .drc files and compare.

    python3 -I tools/fixtures/make_draco_fixtures.py dataset/draco
"""
import json, math, os, sys
import numpy as np
import DracoPy

out = sys.argv[1]
os.makedirs(out, exist_ok=True)

def grid(n, m, holes=()):
    pts, uv, faces = [], [], []
    for j in range(m + 1):
        for i in range(n + 1):
            pts.append((i, j, math.sin(i * 0.7) * math.cos(j * 0.5)))
            uv.append((i / n, j / m))
    for j in range(m):
        for i in range(n):
            if (i, j) in holes:
                continue
            a = j * (n + 1) + i
            faces.append((a, a + 1, a + n + 2)); faces.append((a, a + n + 2, a + n + 1))
    return np.array(pts, np.float32), np.array(uv, np.float64), np.array(faces, np.uint32)

def sphere(nu, nv):
    # Longitude seam: the first and last column are separate vertices (same position, different u).
    pts, uv, nor, faces = [], [], [], []
    for j in range(nv + 1):
        t = math.pi * j / nv
        for i in range(nu + 1):
            p = 2 * math.pi * i / nu
            x, y, z = math.sin(t) * math.cos(p), math.cos(t), math.sin(t) * math.sin(p)
            pts.append((x * 2, y * 2, z * 2)); nor.append((x, y, z)); uv.append((i / nu, j / nv))
    for j in range(nv):
        for i in range(nu):
            a = j * (nu + 1) + i
            if j > 0: faces.append((a, a + nu + 1, a + 1))
            if j < nv - 1: faces.append((a + 1, a + nu + 1, a + nu + 2))
    return np.array(pts, np.float32), np.array(uv, np.float64), np.array(nor, np.float64), np.array(faces, np.uint32)

def cube():
    # Hard edges: every face has its own four vertices (positions repeat, normals differ).
    pts, nor, uv, faces = [], [], [], []
    for axis in range(3):
        for sign in (-1, 1):
            n = [0, 0, 0]; n[axis] = sign
            u = [0, 0, 0]; u[(axis + 1) % 3] = 1
            v = [0, 0, 0]; v[(axis + 2) % 3] = 1
            base = len(pts)
            for a, b in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
                pts.append(tuple(n[k] + a * u[k] + b * v[k] for k in range(3))); nor.append(tuple(n)); uv.append(((a + 1) / 2, (b + 1) / 2))
            f = [(0, 1, 2), (0, 2, 3)] if sign > 0 else [(0, 2, 1), (0, 3, 2)]
            faces += [tuple(base + i for i in t) for t in f]
    return np.array(pts, np.float32), np.array(uv, np.float64), np.array(nor, np.float64), np.array(faces, np.uint32)

cases = {}
g, guv, gf = grid(12, 9, holes={(3, 3), (4, 3), (8, 6)})
cases['grid-holes'] = dict(points=g, faces=gf, tex_coord=guv)
s, suv, snor, sf = sphere(16, 10)
cases['sphere-seam'] = dict(points=s, faces=sf, tex_coord=suv, normals=snor)
c, cuv, cnor, cf = cube()
cases['cube-hard'] = dict(points=c, faces=cf, tex_coord=cuv, normals=cnor)
# Above 1000 faces the encoder switches from the standard to the valence EdgeBreaker coding.
g2, g2uv, g2f = grid(40, 30, holes={(5, 5), (6, 5), (20, 14), (21, 14), (22, 14), (30, 22)})
cases['big-grid'] = dict(points=g2, faces=g2f, tex_coord=g2uv)
s2, s2uv, s2nor, s2f = sphere(40, 30)
cases['big-sphere'] = dict(points=s2, faces=s2f, tex_coord=s2uv, normals=s2nor)
cases['cube-colors'] = dict(points=c, faces=cf, colors=(np.arange(len(c) * 4).reshape(len(c), 4) * 7 % 256).astype(np.uint8))

manifest = []
for name, data in cases.items():
    for level in (0, 1, 3, 5, 7, 10):
        for qb in (8, 14):
            fname = f'{name}-cl{level}-q{qb}.drc'
            kw = dict(quantization_bits=qb, compression_level=level)
            if 'tex_coord' in data: kw['tex_coord_quantization_bits'] = qb
            if 'normals' in data: kw['normal_quantization_bits'] = min(qb, 10)
            if qb == 8 and level not in (0, 5):
                continue
            if name.startswith('big-') and (qb != 14 or level not in (3, 7, 10)):
                continue
            blob = DracoPy.encode(data['points'], data['faces'], **{k: v for k, v in data.items() if k not in ('points', 'faces')}, **kw)
            m = DracoPy.decode(blob)
            exp = {'points': np.asarray(m.points).round(5).tolist(), 'faces': np.asarray(m.faces).tolist()}
            if getattr(m, 'normals', None) is not None and len(m.normals): exp['normals'] = np.asarray(m.normals).round(5).tolist()
            if getattr(m, 'tex_coord', None) is not None and len(m.tex_coord): exp['tex_coord'] = np.asarray(m.tex_coord).round(5).tolist()
            if getattr(m, 'colors', None) is not None and len(m.colors): exp['colors'] = np.asarray(m.colors).tolist()
            open(os.path.join(out, fname), 'wb').write(blob)
            json.dump(exp, open(os.path.join(out, fname[:-4] + '.json'), 'w'), separators=(',', ':'))
            manifest.append(fname)

# glTF files with KHR_draco_mesh_compression: the sphere with normals and texture coordinates, and one with an extra (generic) tangent attribute.
import struct

def glb(gltf, blob):
    j = json.dumps(gltf, separators=(',', ':')).encode()
    j += b' ' * (-len(j) % 4)
    blob += b'\0' * (-len(blob) % 4)
    body = struct.pack('<II', len(j), 0x4E4F534A) + j + struct.pack('<II', len(blob), 0x004E4942) + blob
    return struct.pack('<III', 0x46546C67, 2, 12 + len(body)) + body

def draco_glb(name, with_tangent):
    extra = {}
    if with_tangent:
        extra['generic_attributes'] = {42: np.tile(np.array([1, 0, 0, 1], np.float32), (len(s), 1))}
    blob = DracoPy.encode(s, sf, tex_coord=suv, normals=snor, quantization_bits=14, compression_level=7, tex_coord_quantization_bits=12, normal_quantization_bits=10, **extra)
    m = DracoPy.decode(blob)
    ids = {{0: 'POSITION', 1: 'NORMAL', 3: 'TEXCOORD_0', 4: 'TANGENT'}[a['attribute_type']]: a['unique_id'] for a in m.attributes}
    pts = np.asarray(m.points)
    count = len(pts)
    accessors = [{'componentType': 5123 if count < 65536 else 5125, 'count': int(len(m.faces) * 3), 'type': 'SCALAR'},
                 {'componentType': 5126, 'count': count, 'type': 'VEC3', 'min': pts.min(0).tolist(), 'max': pts.max(0).tolist()},
                 {'componentType': 5126, 'count': count, 'type': 'VEC3'}, {'componentType': 5126, 'count': count, 'type': 'VEC2'}]
    attrs = {'POSITION': 1, 'NORMAL': 2, 'TEXCOORD_0': 3}
    if with_tangent:
        accessors.append({'componentType': 5126, 'count': count, 'type': 'VEC4'}); attrs['TANGENT'] = 4
    gltf = {'asset': {'version': '2.0', 'generator': 'make_draco_fixtures.py'}, 'scene': 0, 'scenes': [{'nodes': [0]}], 'nodes': [{'name': 'sphere', 'mesh': 0}],
            'meshes': [{'primitives': [{'attributes': attrs, 'indices': 0, 'material': 0, 'mode': 4,
                                         'extensions': {'KHR_draco_mesh_compression': {'bufferView': 0, 'attributes': {k: ids[k] for k in attrs}}}}]}],
            'materials': [{'name': 'matte', 'pbrMetallicRoughness': {'baseColorFactor': [0.8, 0.3, 0.2, 1], 'metallicFactor': 0}}],
            'accessors': accessors, 'bufferViews': [{'buffer': 0, 'byteOffset': 0, 'byteLength': len(blob)}], 'buffers': [{'byteLength': len(blob)}],
            'extensionsUsed': ['KHR_draco_mesh_compression'], 'extensionsRequired': ['KHR_draco_mesh_compression']}
    open(os.path.join(out, name), 'wb').write(glb(gltf, blob))
    exp = {'points': pts.round(5).tolist(), 'faces': np.asarray(m.faces).tolist(), 'normals': np.asarray(m.normals).round(5).tolist(), 'tex_coord': np.asarray(m.tex_coord).round(5).tolist()}
    json.dump(exp, open(os.path.join(out, name[:-4] + '.expected.json'), 'w'), separators=(',', ':'))

draco_glb('sphere-draco.glb', False)
draco_glb('sphere-draco-tangent.glb', True)
print(len(manifest), 'files')
