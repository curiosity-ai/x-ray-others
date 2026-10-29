#!/usr/bin/env python3
"""Writes the hand-made fixtures of dataset/synthetic/ byte for byte (original work, CC0).

    python3 -I tools/fixtures/make_fixtures.py dataset/synthetic

The LAS files are written with laspy (pip install laspy), so an independent implementation of the format produced them; without
laspy they are left as committed.

Each file isolates one behaviour of one reader: winding and welding (STL), groups, materials, quads and
negative indices (OBJ), the three PLY encodings (ASCII, little and big endian), colours, point clouds,
component transforms and base materials (3MF), node hierarchy, extras and a mesh shared by two nodes (glTF).
"""
import base64, datetime, json, struct, sys, zipfile, os

out = sys.argv[1]
os.makedirs(out, exist_ok=True)

# A unit cube, 8 corners, 12 triangles with outward (counter-clockwise) winding.
V = [(0,0,0),(1,0,0),(1,1,0),(0,1,0),(0,0,1),(1,0,1),(1,1,1),(0,1,1)]
T = [(0,2,1),(0,3,2),(4,5,6),(4,6,7),(0,1,5),(0,5,4),(1,2,6),(1,6,5),(2,3,7),(2,7,6),(3,0,4),(3,4,7)]

def normal(a, b, c):
    u = [b[i]-a[i] for i in range(3)]; v = [c[i]-a[i] for i in range(3)]
    n = [u[1]*v[2]-u[2]*v[1], u[2]*v[0]-u[0]*v[2], u[0]*v[1]-u[1]*v[0]]
    l = sum(x*x for x in n) ** 0.5
    return [x/l for x in n]

def write(name, data):
    mode = 'wb' if isinstance(data, bytes) else 'w'
    with open(os.path.join(out, name), mode, **({} if mode == 'wb' else {'newline': '\n'})) as f:
        f.write(data)

# ---- STL
s = ['solid unit cube']
for t in T:
    n = normal(*[V[i] for i in t])
    s.append('  facet normal %g %g %g\n    outer loop' % tuple(n))
    for i in t: s.append('      vertex %g %g %g' % V[i])
    s.append('    endloop\n  endfacet')
s.append('endsolid unit cube')
write('cube-ascii.stl', '\n'.join(s) + '\n')
b = bytearray(b'unit cube (binary)'.ljust(80, b'\0')) + struct.pack('<I', len(T))
for t in T:
    b += struct.pack('<3f', *normal(*[V[i] for i in t]))
    for i in t: b += struct.pack('<3f', *V[i])
    b += b'\0\0'
write('cube-binary.stl', bytes(b))

# ---- OBJ: two objects, a quad, normals, a material switch, negative indices, comments
write('scene.obj', '''# two objects: a quad plate and a triangle, with materials
mtllib scene.mtl
v 0 0 0
v 2 0 0
v 2 2 0
v 0 2 0
v 0 0 1
v 2 0 1
v 1 2 1
vn 0 0 1
vn 0 1 0
o plate
usemtl steel
f 1//1 2//1 3//1 4//1
o wedge
usemtl paint
f 5/1/2 6/1/2 7/1/2
usemtl steel
f -3 -2 -1
''')

# ---- PLY
def ply_header(fmt, extra_vertex=''):
    return ('ply\nformat %s 1.0\ncomment unit cube\nelement vertex 8\nproperty float x\nproperty float y\nproperty float z\n%s'
            'element face 12\nproperty list uchar int vertex_indices\nend_header\n') % (fmt, extra_vertex)
col = ('property uchar red\nproperty uchar green\nproperty uchar blue\n')
lines = [ply_header('ascii', col)]
for i, v in enumerate(V): lines.append('%g %g %g %d %d %d\n' % (*v, 32*i, 255-32*i, 128))
for t in T: lines.append('3 %d %d %d\n' % t)
write('cube-ascii.ply', ''.join(lines))
for endian, tag in (('<', 'little'), ('>', 'big')):
    b = bytearray(ply_header('binary_%s_endian' % tag, col).encode())
    for i, v in enumerate(V): b += struct.pack(endian + '3f3B', *v, 32*i, 255-32*i, 128)
    for t in T: b += struct.pack(endian + 'B3i', 3, *t)
    write('cube-binary-%s.ply' % tag, bytes(b))
write('points.ply', 'ply\nformat ascii 1.0\nelement vertex 3\nproperty float x\nproperty float y\nproperty float z\nend_header\n0 0 0\n1 0 0\n0 1 0\n')

# ---- 3MF: one mesh object, one assembly object with two transformed components, base materials
model = '''<?xml version="1.0" encoding="UTF-8"?>
<model unit="millimeter" xml:lang="en-US" xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02">
 <metadata name="Title">Two cubes</metadata>
 <metadata name="Designer">fixture script</metadata>
 <resources>
  <basematerials id="1"><base name="Red" displaycolor="#FF0000FF"/><base name="Blue" displaycolor="#0000FFFF"/></basematerials>
  <object id="2" type="model" name="cube" pid="1" pindex="0"><mesh>
   <vertices>%s</vertices>
   <triangles>%s</triangles></mesh></object>
  <object id="3" type="model" name="pair"><components>
   <component objectid="2"/>
   <component objectid="2" transform="1 0 0 0 1 0 0 0 1 2 0 0"/></components></object>
 </resources>
 <build><item objectid="3" partnumber="PAIR-1" transform="1 0 0 0 1 0 0 0 1 0 5 0"/></build>
</model>''' % (''.join('<vertex x="%g" y="%g" z="%g"/>' % v for v in V), ''.join('<triangle v1="%d" v2="%d" v3="%d"/>' % t for t in T))
with zipfile.ZipFile(os.path.join(out, 'two-cubes.3mf'), 'w', zipfile.ZIP_DEFLATED) as z:
    for info_name, data in (
        ('[Content_Types].xml', '<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="model" ContentType="application/vnd.ms-package.3dmanufacturing-3dmodel+xml"/></Types>'),
        ('_rels/.rels', '<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Target="/3D/3dmodel.model" Id="rel0" Type="http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel"/></Relationships>'),
        ('3D/3dmodel.model', model)):
        zi = zipfile.ZipInfo(info_name, (2026, 1, 1, 0, 0, 0)); zi.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(zi, data)

# ---- glTF: root -> two instances of one mesh, with extras and a transform; a second unrelated mesh
pos = b''.join(struct.pack('<3f', *v) for v in V)
idx = b''.join(struct.pack('<3H', *t) for t in T)
buf = pos + idx
gltf = {
    'asset': {'version': '2.0', 'generator': 'fixture script'},
    'scene': 0, 'scenes': [{'name': 'Cubes', 'nodes': [0]}],
    'nodes': [
        {'name': 'rack', 'children': [1, 2], 'extras': {'partNumber': 'RACK-1'}},
        {'name': 'cube-left', 'mesh': 0},
        {'name': 'cube-right', 'mesh': 0, 'translation': [3, 0, 0], 'scale': [2, 2, 2]}],
    'meshes': [{'name': 'cube', 'primitives': [{'attributes': {'POSITION': 0}, 'indices': 1, 'material': 0}]}],
    'materials': [{'name': 'orange', 'pbrMetallicRoughness': {'baseColorFactor': [1, 0.5, 0, 1], 'metallicFactor': 0}}],
    'accessors': [
        {'bufferView': 0, 'componentType': 5126, 'count': 8, 'type': 'VEC3', 'min': [0, 0, 0], 'max': [1, 1, 1]},
        {'bufferView': 1, 'componentType': 5123, 'count': 36, 'type': 'SCALAR'}],
    'bufferViews': [{'buffer': 0, 'byteOffset': 0, 'byteLength': len(pos)}, {'buffer': 0, 'byteOffset': len(pos), 'byteLength': len(idx)}],
    'buffers': [{'byteLength': len(buf), 'uri': 'data:application/octet-stream;base64,' + base64.b64encode(buf).decode()}],
}
write('instances.gltf', json.dumps(gltf, indent=1) + '\n')

# ---- DXF: layers, a block with an array insert, bulge arc, circle, arc, 3D face, polyface, MESH, spline, text
def dxf(*pairs):
    return ''.join('%d\n%s\n' % (c, v) for c, v in pairs)

header = dxf((0, 'SECTION'), (2, 'HEADER'), (9, '$ACADVER'), (1, 'AC1014'), (9, '$INSUNITS'), (70, 6), (0, 'ENDSEC'))
tables = dxf(
    (0, 'SECTION'), (2, 'TABLES'), (0, 'TABLE'), (2, 'LAYER'), (70, 3),
    (0, 'LAYER'), (2, 'L1'), (70, 0), (62, 1), (6, 'CONTINUOUS'),
    (0, 'LAYER'), (2, 'L2'), (70, 0), (62, 5), (6, 'CONTINUOUS'),
    (0, 'LAYER'), (2, 'HIDDEN'), (70, 0), (62, -3), (6, 'CONTINUOUS'),
    (0, 'ENDTAB'), (0, 'ENDSEC'))
blocks = dxf(
    (0, 'SECTION'), (2, 'BLOCKS'),
    (0, 'BLOCK'), (8, '0'), (2, 'BOX'), (70, 0), (10, 0.0), (20, 0.0), (30, 0.0), (3, 'BOX'),
    (0, 'LINE'), (8, '0'), (10, 0.0), (20, 0.0), (30, 0.0), (11, 1.0), (21, 0.0), (31, 0.0),
    (0, 'LINE'), (8, '0'), (10, 1.0), (20, 0.0), (30, 0.0), (11, 1.0), (21, 1.0), (31, 0.0),
    (0, 'LINE'), (8, '0'), (10, 1.0), (20, 1.0), (30, 0.0), (11, 0.0), (21, 1.0), (31, 0.0),
    (0, 'LINE'), (8, '0'), (10, 0.0), (20, 1.0), (30, 0.0), (11, 0.0), (21, 0.0), (31, 0.0),
    (0, 'ENDBLK'), (8, '0'),
    (0, 'ENDSEC'))
entities = dxf(
    (0, 'SECTION'), (2, 'ENTITIES'),
    (0, 'INSERT'), (8, 'L1'), (2, 'BOX'), (10, 10.0), (20, 0.0), (30, 0.0), (41, 2.0), (42, 2.0), (43, 1.0), (50, 90.0),
    (0, 'INSERT'), (8, 'L2'), (2, 'BOX'), (10, 0.0), (20, 20.0), (30, 0.0), (70, 3), (71, 2), (44, 5.0), (45, 5.0),
    (0, 'LWPOLYLINE'), (100, 'AcDbEntity'), (8, 'L1'), (100, 'AcDbPolyline'), (90, 2), (70, 0), (10, 0.0), (20, 0.0), (42, 1.0), (10, 2.0), (20, 0.0),
    (0, 'CIRCLE'), (8, 'L2'), (10, 0.0), (20, 0.0), (30, 0.0), (40, 3.0),
    (0, 'ARC'), (8, 'L2'), (10, 0.0), (20, 0.0), (30, 0.0), (40, 6.0), (50, 0.0), (51, 90.0),
    (0, '3DFACE'), (8, 'L1'), (10, 0.0), (20, 0.0), (30, 0.0), (11, 1.0), (21, 0.0), (31, 0.0), (12, 1.0), (22, 1.0), (32, 1.0), (13, 0.0), (23, 1.0), (33, 1.0),
    (0, 'POLYLINE'), (8, 'L1'), (66, 1), (70, 64), (71, 4), (72, 2),
    (0, 'VERTEX'), (8, 'L1'), (10, 0.0), (20, 0.0), (30, 5.0), (70, 192),
    (0, 'VERTEX'), (8, 'L1'), (10, 1.0), (20, 0.0), (30, 5.0), (70, 192),
    (0, 'VERTEX'), (8, 'L1'), (10, 1.0), (20, 1.0), (30, 5.0), (70, 192),
    (0, 'VERTEX'), (8, 'L1'), (10, 0.0), (20, 1.0), (30, 6.0), (70, 192),
    (0, 'VERTEX'), (8, 'L1'), (10, 0.0), (20, 0.0), (30, 0.0), (70, 128), (71, 1), (72, 2), (73, 3),
    (0, 'VERTEX'), (8, 'L1'), (10, 0.0), (20, 0.0), (30, 0.0), (70, 128), (71, 1), (72, 3), (73, 4),
    (0, 'SEQEND'), (8, 'L1'),
    (0, 'SPLINE'), (100, 'AcDbEntity'), (8, 'L2'), (100, 'AcDbSpline'), (70, 8), (71, 2), (72, 6), (73, 3), (74, 0),
    (40, 0.0), (40, 0.0), (40, 0.0), (40, 1.0), (40, 1.0), (40, 1.0),
    (10, 0.0), (20, 30.0), (30, 0.0), (10, 5.0), (20, 40.0), (30, 0.0), (10, 10.0), (20, 30.0), (30, 0.0),
    (0, 'TEXT'), (8, 'L1'), (10, 0.0), (20, 50.0), (30, 0.0), (40, 2.5), (1, 'Hello DXF'),
    (0, 'LINE'), (8, 'HIDDEN'), (10, 0.0), (20, 0.0), (30, 0.0), (11, 0.0), (21, 0.0), (31, 7.0),
    (0, 'ENDSEC'), (0, 'EOF'))
write('entities.dxf', header + tables + blocks + entities)

# ---- point clouds: LAS (written with laspy when available, so an independent writer), PCD in three encodings, XYZ, PTS
import math
cloud = [(500000.0 + i * 0.25 % 10, 4000000.0 + (i * 7) % 13 * 0.5, 100.0 + (i % 5) * 1.5) for i in range(200)]
colour = [(i * 5 % 256, 255 - i * 3 % 256, i * 11 % 256) for i in range(200)]

try:
    import laspy, numpy as np
    for fmt, name in ((0, 'cloud-f0.las'), (3, 'cloud-f3.las')):
        header = laspy.LasHeader(point_format=fmt, version='1.2')
        header.offsets = np.array([500000.0, 4000000.0, 0.0])
        header.scales = np.array([0.001, 0.001, 0.001])
        header.date = datetime.date(2026, 1, 15)
        header.generating_software = 'xray3d fixtures'
        header.system_identifier = 'fixture'
        # GeoKeyDirectory: version 1.1.0, one key: ProjectedCSTypeGeoKey (3072) = EPSG:32632
        geokeys = struct.pack('<8H', 1, 1, 0, 1, 3072, 0, 1, 32632)
        header.vlrs.append(laspy.VLR(user_id='LASF_Projection', record_id=34735, description='GeoKeyDirectoryTag', record_data=geokeys))
        las = laspy.LasData(header)
        las.x = np.array([p[0] for p in cloud]); las.y = np.array([p[1] for p in cloud]); las.z = np.array([p[2] for p in cloud])
        if fmt == 3:
            las.red = np.array([c[0] * 257 for c in colour], dtype=np.uint16)
            las.green = np.array([c[1] * 257 for c in colour], dtype=np.uint16)
            las.blue = np.array([c[2] * 257 for c in colour], dtype=np.uint16)
        las.write(os.path.join(out, name))
except ImportError:
    pass  # the committed files stay as they are; they only change when laspy is present

def pcd_header(fields, sizes, types, n, kind, extra=''):
    return ('# .PCD v0.7 - Point Cloud Data file format\nVERSION 0.7\nFIELDS %s\nSIZE %s\nTYPE %s\nCOUNT %s\nWIDTH %d\nHEIGHT 1\nVIEWPOINT 0 0 0 1 0 0 0\nPOINTS %d\nDATA %s\n'
            % (' '.join(fields), ' '.join(map(str, sizes)), ' '.join(types), ' '.join('1' for _ in fields), n, n, kind))

pts = [(math.cos(i / 10) * 2, math.sin(i / 10) * 2, i / 50) for i in range(100)]
rgb = [struct.unpack('<f', struct.pack('<I', (c[0] << 16) | (c[1] << 8) | c[2]))[0] for c in colour[:100]]
write('cloud-ascii.pcd', pcd_header(['x', 'y', 'z', 'rgb'], [4, 4, 4, 4], ['F', 'F', 'F', 'F'], 100, 'ascii') + ''.join('%.6f %.6f %.6f %.9g\n' % (p + (c,)) for p, c in zip(pts, rgb)))
rows = b''.join(struct.pack('<4f', *p, c) for p, c in zip(pts, rgb))
write('cloud-binary.pcd', pcd_header(['x', 'y', 'z', 'rgb'], [4, 4, 4, 4], ['F', 'F', 'F', 'F'], 100, 'binary').encode() + rows)

def lzf_compress(data):
    out = bytearray(); i = 0; lit = bytearray(); table = {}
    def flush():
        nonlocal lit
        while lit:
            chunk = lit[:32]; lit = lit[32:]
            out.append(len(chunk) - 1); out.extend(chunk)
    while i < len(data):
        if i + 2 < len(data):
            key = bytes(data[i:i + 3]); ref = table.get(key); table[key] = i
            if ref is not None and 0 < i - ref <= 8191:
                length = 3
                while i + length < len(data) and length < 264 and data[ref + length] == data[i + length]:
                    length += 1
                flush()
                off = i - ref - 1; l = length - 2
                if l < 7: out.append((l << 5) | (off >> 8))
                else: out.append((7 << 5) | (off >> 8)); out.append(l - 7)
                out.append(off & 255)
                i += length
                continue
        lit.append(data[i]); i += 1
    flush()
    return bytes(out)

planar = b''.join(b''.join(struct.pack('<f', v[k]) for v in [p + (c,) for p, c in zip(pts, rgb)]) for k in range(4))
packed = lzf_compress(planar)
write('cloud-compressed.pcd', pcd_header(['x', 'y', 'z', 'rgb'], [4, 4, 4, 4], ['F', 'F', 'F', 'F'], 100, 'binary_compressed').encode() + struct.pack('<II', len(packed), len(planar)) + packed)

write('cloud.xyz', '# x y z r g b\n' + ''.join('%.4f %.4f %.4f %d %d %d\n' % (p + c) for p, c in zip(pts, colour)))
write('cloud.pts', '100\n' + ''.join('%.4f %.4f %.4f %d %d %d %d\n' % (p + (500,) + c) for p, c in zip(pts, colour)))

# ---- OFF, AMF, 3DS, COLLADA, X3D and VRML scenes built from the same cube and a pyramid
CUBE_QUADS = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
write('cube.off', 'OFF\n8 6 12\n' + ''.join('%g %g %g\n' % v for v in V) + ''.join('4 %d %d %d %d\n' % q for q in CUBE_QUADS))
write('cube-colored.off', 'COFF\n# vertex colours\n8 6 0\n' + ''.join('%g %g %g %d %d %d 255\n' % (v + (40 * i, 255 - 30 * i, 128)) for i, v in enumerate(V)) + ''.join('4 %d %d %d %d\n' % q for q in CUBE_QUADS))
write('cube-facecolors.off', 'COFF\n8 6 0\n' + ''.join('%g %g %g 255 255 255 255\n' % v for v in V)
      + ''.join('4 %d %d %d %d %d %d %d 255\n' % (q + ((255, 0, 0) if i < 3 else (0, 0, 255))) for i, q in enumerate(CUBE_QUADS)))

PYRAMID_V = [(0, 0, 0), (2, 0, 0), (2, 2, 0), (0, 2, 0), (1, 1, 3)]
PYRAMID_T = [(0, 2, 1), (0, 3, 2), (0, 1, 4), (1, 2, 4), (2, 3, 4), (3, 0, 4)]

amf = '''<?xml version="1.0" encoding="UTF-8"?>
<amf unit="millimeter" version="1.1">
 <metadata type="name">fixture amf</metadata>
 <material id="1"><metadata type="name">Red</metadata><color><r>1</r><g>0</g><b>0</b><a>1</a></color></material>
 <material id="2"><metadata type="name">Blue</metadata><color><r>0</r><g>0</g><b>1</b><a>1</a></color></material>
 <object id="0"><metadata type="name">cube</metadata><mesh>
  <vertices>%s</vertices>
  <volume materialid="1">%s</volume>
  <volume materialid="2">%s</volume>
 </mesh></object>
 <constellation id="0">
  <instance objectid="0"><deltax>0</deltax><deltay>0</deltay><deltaz>0</deltaz><rx>0</rx><ry>0</ry><rz>0</rz></instance>
  <instance objectid="0"><deltax>5</deltax><deltay>0</deltay><deltaz>0</deltaz><rx>0</rx><ry>0</ry><rz>90</rz></instance>
 </constellation>
</amf>
''' % (''.join('<vertex><coordinates><x>%g</x><y>%g</y><z>%g</z></coordinates></vertex>' % v for v in V),
       ''.join('<triangle><v1>%d</v1><v2>%d</v2><v3>%d</v3></triangle>' % t for t in T[:6]),
       ''.join('<triangle><v1>%d</v1><v2>%d</v2><v3>%d</v3></triangle>' % t for t in T[6:]))
write('cube.amf', amf)
with zipfile.ZipFile(os.path.join(out, 'cube-zipped.amf'), 'w', zipfile.ZIP_DEFLATED) as z:
    zi = zipfile.ZipInfo('cube.amf', (2026, 1, 1, 0, 0, 0)); zi.compress_type = zipfile.ZIP_DEFLATED
    z.writestr(zi, amf)

def chunk(cid, payload=b''):
    return struct.pack('<HI', cid, 6 + len(payload)) + payload
def cstr(s): return s.encode('latin1') + b'\0'
def trimesh(name, verts, tris, material, mats):
    vl = chunk(0x4110, struct.pack('<H', len(verts)) + b''.join(struct.pack('<3f', *v) for v in verts))
    fm = b''.join(chunk(0x4130, cstr(m) + struct.pack('<H', len(ids)) + b''.join(struct.pack('<H', i) for i in ids)) for m, ids in mats)
    fl = chunk(0x4120, struct.pack('<H', len(tris)) + b''.join(struct.pack('<4H', *t, 0) for t in tris) + fm)
    return chunk(0x4000, cstr(name) + chunk(0x4100, vl + fl))
def material(name, rgb):
    return chunk(0xAFFF, chunk(0xA000, cstr(name)) + chunk(0xA020, chunk(0x0011, bytes(rgb))))
editor = chunk(0x3D3E, struct.pack('<I', 3)) + material('red', (255, 0, 0)) + material('green', (0, 255, 0)) \
    + trimesh('cube', V, T, 'red', [('red', list(range(6))), ('green', list(range(6, 12)))]) \
    + trimesh('pyramid', [(x + 5, y, z) for x, y, z in PYRAMID_V], PYRAMID_T, 'green', [('green', list(range(6)))])
write('scene.3ds', chunk(0x4D4D, chunk(0x0002, struct.pack('<I', 3)) + chunk(0x3D3D, editor)))

def floats(vals): return ' '.join('%g' % v for v in vals)
dae = '''<?xml version="1.0" encoding="utf-8"?>
<COLLADA xmlns="http://www.collada.org/2005/11/COLLADASchema" version="1.4.1">
 <asset><contributor><authoring_tool>fixture script</authoring_tool></contributor><created>2026-01-01T00:00:00Z</created><modified>2026-01-01T00:00:00Z</modified><unit name="centimeter" meter="0.01"/><up_axis>Z_UP</up_axis></asset>
 <library_effects>
  <effect id="red-fx"><profile_COMMON><technique sid="t"><phong><diffuse><color>1 0 0 1</color></diffuse></phong></technique></profile_COMMON></effect>
  <effect id="blue-fx"><profile_COMMON><technique sid="t"><lambert><diffuse><color>0 0 1 1</color></diffuse></lambert></technique></profile_COMMON></effect>
 </library_effects>
 <library_materials>
  <material id="red" name="Red"><instance_effect url="#red-fx"/></material>
  <material id="blue" name="Blue"><instance_effect url="#blue-fx"/></material>
 </library_materials>
 <library_geometries>
  <geometry id="cube-geom" name="cube"><mesh>
   <source id="cube-pos"><float_array id="cube-pos-a" count="24">%s</float_array><technique_common><accessor source="#cube-pos-a" count="8" stride="3"><param name="X" type="float"/><param name="Y" type="float"/><param name="Z" type="float"/></accessor></technique_common></source>
   <vertices id="cube-vtx"><input semantic="POSITION" source="#cube-pos"/></vertices>
   <polylist material="mat" count="6"><input semantic="VERTEX" source="#cube-vtx" offset="0"/><vcount>4 4 4 4 4 4</vcount><p>%s</p></polylist>
  </mesh></geometry>
  <geometry id="pyr-geom" name="pyramid"><mesh>
   <source id="pyr-pos"><float_array id="pyr-pos-a" count="15">%s</float_array><technique_common><accessor source="#pyr-pos-a" count="5" stride="3"><param name="X" type="float"/><param name="Y" type="float"/><param name="Z" type="float"/></accessor></technique_common></source>
   <vertices id="pyr-vtx"><input semantic="POSITION" source="#pyr-pos"/></vertices>
   <triangles material="mat" count="6"><input semantic="VERTEX" source="#pyr-vtx" offset="0"/><p>%s</p></triangles>
  </mesh></geometry>
 </library_geometries>
 <library_visual_scenes><visual_scene id="scene">
  <node id="root" name="root"><translate>1 2 3</translate>
   <node id="cube-node" name="cube-node"><instance_geometry url="#cube-geom"><bind_material><technique_common><instance_material symbol="mat" target="#red"/></technique_common></bind_material></instance_geometry></node>
   <node id="pyr-node" name="pyr-node"><translate>10 0 0</translate><rotate>0 0 1 90</rotate><scale>2 2 2</scale><instance_geometry url="#pyr-geom"><bind_material><technique_common><instance_material symbol="mat" target="#blue"/></technique_common></bind_material></instance_geometry></node>
   <node id="matrix-node" name="matrix-node"><matrix>1 0 0 0 0 1 0 20 0 0 1 0 0 0 0 1</matrix><instance_geometry url="#cube-geom"><bind_material><technique_common><instance_material symbol="mat" target="#blue"/></technique_common></bind_material></instance_geometry></node>
  </node>
 </visual_scene></library_visual_scenes>
 <scene><instance_visual_scene url="#scene"/></scene>
</COLLADA>
''' % (floats([c for v in V for c in v]), ' '.join(str(i) for q in CUBE_QUADS for i in q),
       floats([c for v in PYRAMID_V for c in v]), ' '.join(str(i) for t in PYRAMID_T for i in t))
write('scene.dae', dae)

x3d = '''<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE X3D PUBLIC "ISO//Web3D//DTD X3D 3.3//EN" "http://www.web3d.org/specifications/x3d-3.3.dtd">
<X3D version="3.3" profile="Interchange">
 <head><meta name="title" content="fixture x3d"/></head>
 <Scene>
  <Transform DEF="outer" translation="1 0 0">
   <Shape DEF="cubeShape"><Appearance><Material DEF="red" diffuseColor="1 0 0"/></Appearance>
    <IndexedFaceSet coordIndex="%s"><Coordinate point="%s"/></IndexedFaceSet></Shape>
   <Transform translation="0 5 0" rotation="0 0 1 1.5707963" scale="2 2 2"><Shape USE="cubeShape"/></Transform>
  </Transform>
  <Shape><Appearance><Material diffuseColor="0 0 1" transparency="0.25"/></Appearance><Box size="1 2 3"/></Shape>
  <Shape><IndexedLineSet coordIndex="0 1 2 -1 4 5"><Coordinate point="%s"/></IndexedLineSet></Shape>
 </Scene>
</X3D>
''' % (' '.join(str(i) for q in CUBE_QUADS for i in q + (-1,)), floats([c for v in V for c in v]), floats([c for v in V for c in v]))
write('scene.x3d', x3d)

vrml = '''#VRML V2.0 utf8
# fixture vrml
DEF outer Transform { translation 1 0 0 children [
  DEF cubeShape Shape { appearance Appearance { material DEF red Material { diffuseColor 1 0 0 } }
    geometry IndexedFaceSet { coord Coordinate { point [ %s ] } coordIndex [ %s ] } }
  Transform { translation 0 5 0 rotation 0 0 1 1.5707963 scale 2 2 2 children [ USE cubeShape ] }
] }
Shape { appearance Appearance { material Material { diffuseColor 0 0 1 transparency 0.25 } } geometry Box { size 1 2 3 } }
Shape { geometry IndexedLineSet { coord Coordinate { point [ %s ] } coordIndex [ 0 1 2 -1 4 5 ] } }
''' % (', '.join('%g %g %g' % v for v in V), ', '.join(' '.join(str(i) for i in q) + ' -1' for q in CUBE_QUADS), ', '.join('%g %g %g' % v for v in V))
write('scene.wrl', vrml)
