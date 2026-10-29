#!/usr/bin/env python3
"""Writes the hand-made fixtures of dataset/synthetic/ byte for byte (original work, CC0).

    python3 -I tools/fixtures/make_fixtures.py dataset/synthetic

Each file isolates one behaviour of one reader: winding and welding (STL), groups, materials, quads and
negative indices (OBJ), the three PLY encodings (ASCII, little and big endian), colours, point clouds,
component transforms and base materials (3MF), node hierarchy, extras and a mesh shared by two nodes (glTF).
"""
import base64, json, struct, sys, zipfile, os

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
