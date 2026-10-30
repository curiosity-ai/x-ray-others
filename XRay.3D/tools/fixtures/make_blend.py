"""Writes dataset/synthetic/blend/*.blend (original work, CC0) with Blender's own Python module (bpy), which is also the oracle for them.

    pip install bpy        # python 3.13 wheels exist; use a virtual environment
    python make_blend.py dataset/synthetic/blend

The scene exercises what BlendReader reads: collections (one excluded), a parent with a parent-inverse matrix, every rotation mode, flat and smooth
shading, an ngon and a concave polygon, UVs, two material slots with a Principled BSDF, a hidden object, a collection instance with an offset, a
light and a camera, and a scene unit scale of 0.001. It is saved Zstandard-compressed and gzip-compressed, plus an uncompressed copy without the user-interface blocks (window manager, workspaces, screens, thumbnail).
"""
import gzip, math, os, sys
import bpy
from mathutils import Matrix, Vector, Euler, Quaternion

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
bpy.ops.wm.read_homefile(use_empty=True)
scene = bpy.context.scene
scene.name = 'Synthetic'
scene.unit_settings.scale_length = 0.001
root = scene.collection


def material(name, color, metallic, roughness):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    p = m.node_tree.nodes['Principled BSDF']
    p.inputs['Base Color'].default_value = color
    p.inputs['Metallic'].default_value = metallic
    p.inputs['Roughness'].default_value = roughness
    return m


red = material('Red', (0.8, 0.1, 0.1, 1), 0.2, 0.4)
steel = material('Steel', (0.5, 0.55, 0.6, 1), 1.0, 0.25)


def mesh_object(name, verts, faces, coll, mats=(), smooth=False, uv=True, mat_index=None):
    me = bpy.data.meshes.new(name + 'Mesh')
    me.from_pydata(verts, [], faces)
    me.update()
    for m in mats:
        me.materials.append(m)
    if mat_index:
        for p, i in zip(me.polygons, mat_index):
            p.material_index = i
    for p in me.polygons:
        p.use_smooth = smooth
    if uv:
        layer = me.uv_layers.new(name='UVMap')
        for loop in me.loops:
            v = me.vertices[loop.vertex_index].co
            layer.data[loop.index].uv = (v.x * 0.5 + 0.5, v.y * 0.5 + 0.5)
    o = bpy.data.objects.new(name, me)
    coll.objects.link(o)
    return o


cube_v = [(-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1), (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)]
cube_f = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]

props = bpy.data.collections.new('Props'); root.children.link(props)
crate = mesh_object('Crate', cube_v, cube_f, props, [red, steel], smooth=False, mat_index=[0, 0, 1, 1, 0, 1])
crate.location = (1, 2, 3)
crate.rotation_euler = (0.3, 0.5, 0.7)
crate.scale = (1, 2, 3)

# An octagonal prism: two 8-gon caps (ngons) and eight quads, smooth shaded; a child of the crate with a parent-inverse matrix.
n = 8
ring = [(math.cos(2 * math.pi * i / n), math.sin(2 * math.pi * i / n)) for i in range(n)]
pv = [(x, y, -0.5) for x, y in ring] + [(x, y, 0.5) for x, y in ring]
pf = [tuple(reversed(range(n))), tuple(range(n, 2 * n))] + [(i, (i + 1) % n, n + (i + 1) % n, n + i) for i in range(n)]
prism = mesh_object('Prism', pv, pf, props, [steel], smooth=True)
prism.parent = crate
prism.location = (0, 0, 2)
bpy.context.view_layer.update()
prism.matrix_parent_inverse = crate.matrix_world.inverted()
prism.location = (0.5, 0, 2)
prism.scale = (0.4, 0.4, 0.4)

# A concave L-shaped polygon.
lv = [(0, 0, 0), (3, 0, 0), (3, 1, 0), (1, 1, 0), (1, 3, 0), (0, 3, 0)]
ell = mesh_object('Ell', lv, [(0, 1, 2, 3, 4, 5)], props, [red], uv=True)
ell.location = (-6, 0, 0)

# Rotation modes.
for i, (name, mode) in enumerate([('RotQuat', 'QUATERNION'), ('RotAxis', 'AXIS_ANGLE'), ('RotZYX', 'ZYX'), ('RotYXZ', 'YXZ')]):
    o = mesh_object(name, [(0, 0, 0), (2, 0, 0), (0, 1, 0), (0, 0, 0.5)], [(0, 2, 1), (0, 1, 3), (1, 2, 3), (0, 3, 2)], props, [steel], uv=False)
    o.location = (i * 3, -5, 0)
    o.rotation_mode = mode
    if mode == 'QUATERNION':
        o.rotation_quaternion = Quaternion((1, 2, 3), 0.9)
    elif mode == 'AXIS_ANGLE':
        o.rotation_axis_angle = (0.8, 0.0, 1.0, 1.0)
    else:
        o.rotation_euler = Euler((0.4, 0.6, 0.8), mode)

# An excluded collection and a hidden object.
skip = bpy.data.collections.new('Excluded'); root.children.link(skip)
mesh_object('InExcluded', cube_v, cube_f, skip, [red])
hidden = mesh_object('Hidden', cube_v, cube_f, props, [red])
hidden.hide_viewport = True
bpy.context.view_layer.layer_collection.children['Excluded'].exclude = True

# A collection instanced by an empty, with an instance offset.
widget = bpy.data.collections.new('Widget')
widget.instance_offset = (1, 0, 0)
bpy.context.scene.collection.children.link(widget)
mesh_object('WidgetPart', cube_v, cube_f, widget, [steel], uv=False)
bpy.context.view_layer.layer_collection.children['Widget'].exclude = True
inst = bpy.data.objects.new('WidgetInstance', None)
inst.instance_type = 'COLLECTION'
inst.instance_collection = widget
inst.location = (0, 6, 0)
inst.scale = (0.5, 0.5, 0.5)
props.objects.link(inst)

cam = bpy.data.objects.new('Camera', bpy.data.cameras.new('Camera')); props.objects.link(cam)
lamp = bpy.data.objects.new('Lamp', bpy.data.lights.new('Lamp', 'POINT')); props.objects.link(lamp)

import tempfile
raw = os.path.join(tempfile.mkdtemp(), 'scene-raw.blend')
bpy.ops.wm.save_as_mainfile(filepath=raw, compress=False)
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, 'scene-zstd.blend'), compress=True)


def slim(data):
    """Drops the window-manager, workspace, screen and thumbnail blocks (the user interface, not the scene): the data blocks they own go too."""
    import struct
    hs = int(data[7:9])
    out = bytearray(data[:hs])
    p = hs
    skip = False
    drop = {b'WM', b'WS', b'SN', b'TEST', b'REND', b'WS\0\0'}
    while p < len(data):
        code, sdna, old, ln, nr = struct.unpack('<4siqqq', data[p:p + 32])
        block = data[p:p + 32 + ln]
        c = code.rstrip(b'\0')
        if c == b'DATA':
            if not skip:
                out += block
        else:
            skip = c in drop
            if not skip:
                out += block
        p += 32 + ln
        if c == b'ENDB':
            break
    return bytes(out)


with open(raw, 'rb') as f:
    raw_bytes = f.read()
with open(os.path.join(out, 'scene-raw.blend'), 'wb') as f:
    f.write(slim(raw_bytes))
with open(raw, 'rb') as f, gzip.GzipFile(os.path.join(out, 'scene-gzip.blend'), 'wb', mtime=0) as g:
    g.write(f.read())
