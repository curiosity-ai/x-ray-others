"""Writes the golden values of the units-*.blend and shapes*.blend fixtures from Blender's own Python module (bpy), an independent reader.

    pip install bpy        # python 3.13 wheels exist; use a virtual environment
    python make_blend_units_golden.py <out.json> <file.blend> [<file.blend> ...]

For every file: the unit settings, and for each visible mesh object (instances of collections included) the world-space bounds, area and enclosed
volume in METRES (Blender units times the scene's unit scale; a mirroring object's volume counts as positive). The .NET tests compare these with
what BlendReader plus the GLB writer produce.
"""
import json, os, sys
import bpy, bmesh
from mathutils import Matrix, Vector

out = sys.argv[1]
golden = {}
for path in sys.argv[2:]:
    bpy.ops.wm.read_homefile(use_empty=True)
    bpy.ops.wm.open_mainfile(filepath=path)
    scene = bpy.context.scene
    u = scene.unit_settings
    scale = u.scale_length if u.system != 'NONE' else 1.0
    deps = bpy.context.evaluated_depsgraph_get()
    lo = [1e30] * 3
    hi = [-1e30] * 3
    area = volume = 0.0
    objects = []
    for inst in deps.object_instances:
        o = inst.object
        if o.type != 'MESH' or o.hide_viewport:
            continue
        me = o.data
        bm = bmesh.new()
        bm.from_mesh(me)
        mw = inst.matrix_world.copy()
        det = mw.to_3x3().determinant()
        bmesh.ops.transform(bm, matrix=mw, verts=bm.verts)
        bmesh.ops.triangulate(bm, faces=bm.faces[:])
        a = sum(f.calc_area() for f in bm.faces) * scale * scale
        v = bm.calc_volume(signed=True) * scale ** 3
        if det < 0:
            v = -v
        pts = [vtx.co for vtx in bm.verts]
        mn = [min(p[i] for p in pts) * scale for i in range(3)]
        mx = [max(p[i] for p in pts) * scale for i in range(3)]
        for i in range(3):
            lo[i] = min(lo[i], mn[i]); hi[i] = max(hi[i], mx[i])
        area += a
        volume += v
        objects.append({'name': o.name, 'area': a, 'volume': v, 'min': mn, 'max': mx})
        bm.free()
    golden[os.path.basename(path)] = {
        'blender': bpy.app.version_string, 'system': u.system, 'length_unit': u.length_unit, 'scale_length': u.scale_length,
        'meters_per_unit': scale, 'area': area, 'volume': volume, 'min': lo, 'max': hi, 'objects': sorted(objects, key=lambda x: (x['name'], x['min'])),
    }
with open(out, 'w') as f:
    json.dump(golden, f, indent=1, sort_keys=True)
