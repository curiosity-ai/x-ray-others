"""Writes dataset/synthetic/blend-units/*.blend (original work, CC0) with Blender's own Python module (bpy), which is also the oracle for them.

    pip install bpy        # python 3.13 wheels exist; use a virtual environment
    python make_blend_units.py dataset/synthetic/blend-units

One scene per unit setting, each holding a cube of a known number of Blender units, so the size in metres is the cube's side times the scene's
unit scale (Blender's `scene.unit_settings.scale_length`; the length unit it shows, millimetres or inches, only changes how numbers are printed):

    units-mm.blend     METRIC, MILLIMETERS, scale 0.001, cube 20 BU      -> 20 mm
    units-m.blend      METRIC, METERS, scale 1, cube 2 BU                -> 2 m
    units-cm.blend     METRIC, CENTIMETERS, scale 0.01, cube 30 BU       -> 0.3 m
    units-inch.blend   IMPERIAL, INCHES, scale 0.0254, cube 10 BU        -> 0.254 m
    units-km.blend     METRIC, KILOMETERS, scale 1000, cube 0.5 BU       -> 500 m
    units-odd.blend    METRIC, METERS, scale 0.0125 (no named unit), cube 8 BU -> 0.1 m
    units-none.blend   unit system NONE (scale 0.001 is ignored), cube 2 BU -> 2 m
    shapes.blend       scale 1: closed meshes of known volume (cube, cylinder, UV sphere, torus, Suzanne), a mirrored cube, a parent with a
                       non-uniform scale and a rotated child, an instanced collection with an offset, UV maps and a textured material
    shapes-mm.blend    the same scene at scale 0.001 (every size is in millimetres), saved gzip-compressed

The files are saved Zstandard-compressed except units-mm (uncompressed, without the user-interface blocks) and shapes-mm (gzip).
"""
import gzip, math, os, sys, tempfile
import bpy
from mathutils import Matrix, Vector

out = sys.argv[1]
os.makedirs(out, exist_ok=True)


def fresh(name, system='METRIC', length_unit='METERS', scale=1.0):
    bpy.ops.wm.read_homefile(use_empty=True)
    scene = bpy.context.scene
    scene.name = name
    u = scene.unit_settings
    u.system = system
    if system != 'NONE':
        u.length_unit = length_unit
    u.scale_length = scale
    return scene


def add(scene, obj, collection=None):
    (collection or scene.collection).objects.link(obj)
    return obj


def cube_object(scene, name, size, location=(0, 0, 0)):
    me = bpy.data.meshes.new(name + 'Mesh')
    h = size / 2
    verts = [(x * h, y * h, z * h) for z in (-1, 1) for y in (-1, 1) for x in (-1, 1)]
    faces = [(0, 2, 3, 1), (4, 5, 7, 6), (0, 1, 5, 4), (2, 6, 7, 3), (0, 4, 6, 2), (1, 3, 7, 5)]
    me.from_pydata(verts, [], faces)
    me.update()
    me.validate()
    me.polygons.foreach_set('use_smooth', [False] * len(me.polygons))
    uv = me.uv_layers.new(name='UVMap')
    for loop in me.loops:
        uv.data[loop.index].uv = (0.5 + 0.5 * me.vertices[loop.vertex_index].co.x / h, 0.5 + 0.5 * me.vertices[loop.vertex_index].co.y / h)
    o = bpy.data.objects.new(name, me)
    o.location = location
    return add(scene, o)


def primitive(scene, name, op, location, **kw):
    bpy.ops.object.select_all(action='DESELECT')
    op(location=location, **kw)
    o = bpy.context.active_object
    o.name = name
    for c in list(o.users_collection):
        c.objects.unlink(o)
    scene.collection.objects.link(o)
    return o


def save(path, compress):
    bpy.ops.wm.save_as_mainfile(filepath=path, compress=compress)


def slim(data):
    """Drops the window-manager, workspace, screen and thumbnail blocks (the user interface, not the scene): the data blocks they own go too."""
    import struct
    hs = int(data[7:9])
    res = bytearray(data[:hs])
    p = hs
    skip = False
    drop = {b'WM', b'WS', b'SN', b'TEST', b'REND', b'WS\0\0'}
    while p < len(data):
        code, sdna, old, ln, nr = struct.unpack('<4siqqq', data[p:p + 32])
        block = data[p:p + 32 + ln]
        c = code.rstrip(b'\0')
        if c == b'DATA':
            if not skip:
                res += block
        else:
            skip = c in drop
            if not skip:
                res += block
        p += 32 + ln
        if c == b'ENDB':
            break
    return bytes(res)


# ---- one cube per unit setting ----------------------------------------------------------------------------------------------------------
for fname, system, length_unit, scale, side, compress in [
    ('units-mm.blend', 'METRIC', 'MILLIMETERS', 0.001, 20, 'raw'),
    ('units-m.blend', 'METRIC', 'METERS', 1.0, 2, True),
    ('units-cm.blend', 'METRIC', 'CENTIMETERS', 0.01, 30, True),
    ('units-inch.blend', 'IMPERIAL', 'INCHES', 0.0254, 10, True),
    ('units-km.blend', 'METRIC', 'KILOMETERS', 1000.0, 0.5, True),
    ('units-odd.blend', 'METRIC', 'METERS', 0.0125, 8, True),
    ('units-none.blend', 'NONE', None, 0.001, 2, True),
]:
    scene = fresh('Units', system, length_unit or 'METERS', scale)
    cube_object(scene, 'Cube', side)
    target = os.path.join(out, fname)
    if compress == 'raw':
        tmp = os.path.join(tempfile.mkdtemp(), 'raw.blend')
        save(tmp, False)
        with open(tmp, 'rb') as f, open(target, 'wb') as g:
            g.write(slim(f.read()))
    else:
        save(target, True)


# ---- shapes -------------------------------------------------------------------------------------------------------------------------------
def shapes(name, scale):
    scene = fresh(name, 'METRIC', 'METERS' if scale == 1 else 'MILLIMETERS', scale)
    cube_object(scene, 'Cube', 2, (0, 0, 0))
    primitive(scene, 'Cylinder', bpy.ops.mesh.primitive_cylinder_add, (4, 0, 0), vertices=32, radius=1, depth=2)
    primitive(scene, 'Sphere', bpy.ops.mesh.primitive_uv_sphere_add, (8, 0, 0), segments=32, ring_count=16, radius=1)
    primitive(scene, 'Torus', bpy.ops.mesh.primitive_torus_add, (12, 0, 0), major_segments=32, minor_segments=16, major_radius=1.5, minor_radius=0.5)
    primitive(scene, 'Suzanne', bpy.ops.mesh.primitive_monkey_add, (16, 0, 0), size=2)

    # a cube scaled by -1 along x: its triangles read the other way round, and it is still a solid of volume 8
    mirrored = cube_object(scene, 'Mirrored', 2, (20, 0, 0))
    mirrored.scale = (-1, 1, 1)

    # a parent scaled (1, 2, 3) with a child rotated 45 degrees about z: the child's world matrix has shear
    parent = bpy.data.objects.new('Squash', None)
    parent.location = (0, 6, 0)
    parent.scale = (1, 2, 3)
    add(scene, parent)
    child = cube_object(scene, 'Sheared', 1, (0, 0, 0))
    child.parent = parent
    child.rotation_euler = (0, 0, math.radians(45))

    # an instanced collection: a cube of side 1 at the collection's own origin, instanced twice with offsets
    coll = bpy.data.collections.new('Parts')
    scene.collection.children.link(coll)
    part = cube_object(scene, 'Part', 1, (0, 0, 0))
    scene.collection.objects.unlink(part)
    coll.objects.link(part)
    for i, x in enumerate((6, 10)):
        inst = bpy.data.objects.new('Instance' + str(i), None)
        inst.instance_type = 'COLLECTION'
        inst.instance_collection = coll
        inst.location = (x, 6, 0)
        add(scene, inst)

    # a UV-mapped cube with an image texture material
    textured = cube_object(scene, 'Textured', 2, (16, 6, 0))
    img = bpy.data.images.new('Checker', 4, 4)
    img.pixels = [1.0, 0.2, 0.2, 1.0, 0.2, 0.2, 1.0, 1.0] * 8
    img.pack()
    mat = bpy.data.materials.new('Textured')
    mat.use_nodes = True
    tex = mat.node_tree.nodes.new('ShaderNodeTexImage')
    tex.image = img
    mat.node_tree.links.new(tex.outputs['Color'], mat.node_tree.nodes['Principled BSDF'].inputs['Base Color'])
    textured.data.materials.append(mat)
    return scene


shapes('Shapes', 1.0)
save(os.path.join(out, 'shapes.blend'), True)
shapes('ShapesMm', 0.001)
tmp = os.path.join(tempfile.mkdtemp(), 'raw.blend')
save(tmp, False)
with open(tmp, 'rb') as f, gzip.GzipFile(os.path.join(out, 'shapes-mm.blend'), 'wb', mtime=0) as g:
    g.write(f.read())
