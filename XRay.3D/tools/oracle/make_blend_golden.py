"""Writes the golden values of .blend files from Blender's own Python module (bpy), an independent reader.

    pip install bpy        # in a virtual environment; python 3.13 wheels exist
    python make_blend_golden.py <out.json> <file.blend> [<file.blend> ...]

For every file: Blender's version, the scene name, and for each mesh object that is visible in the first view layer (not excluded, not hidden in
the viewport): its name, parent, mesh name, vertex / polygon / triangle counts, material names and the world-space bounds of the base mesh
(modifiers not applied). The .NET tests compare these with what BlendReader returns.
"""
import json, os, sys
import bpy
import mathutils

out = sys.argv[1]
golden = {}
for path in sys.argv[2:]:
    bpy.ops.wm.read_homefile(use_empty=True)
    bpy.ops.wm.open_mainfile(filepath=path)
    scene = bpy.context.scene
    layer = bpy.context.view_layer
    objects = []
    total_tris = 0
    lo = [1e30] * 3
    hi = [-1e30] * 3
    for o in layer.objects:
        if o.type != 'MESH' or o.hide_viewport:
            continue
        me = o.data
        me.calc_loop_triangles()
        mw = o.matrix_world
        pts = [mw @ v.co for v in me.vertices]
        if not pts:
            continue
        mn = [min(p[i] for p in pts) for i in range(3)]
        mx = [max(p[i] for p in pts) for i in range(3)]
        for i in range(3):
            lo[i] = min(lo[i], mn[i]); hi[i] = max(hi[i], mx[i])
        total_tris += len(me.loop_triangles)
        objects.append({
            'name': o.name, 'parent': o.parent.name if o.parent else None, 'mesh': me.name,
            'vertices': len(me.vertices), 'polygons': len(me.polygons), 'triangles': len(me.loop_triangles),
            'fan_triangles': sum(len(p.vertices) - 2 for p in me.polygons),
            'uv_layers': len(me.uv_layers), 'materials': [m.name if m else None for m in me.materials],
            'min': mn, 'max': mx,
        })
    mats = {}
    for m in bpy.data.materials:
        entry = {'diffuse': list(m.diffuse_color)}
        if m.use_nodes and m.node_tree:
            for n in m.node_tree.nodes:
                if n.bl_idname == 'ShaderNodeBsdfPrincipled':
                    entry['base_color'] = list(n.inputs['Base Color'].default_value)
                    entry['metallic'] = n.inputs['Metallic'].default_value
                    entry['roughness'] = n.inputs['Roughness'].default_value
                    break
        mats[m.name] = entry
    golden[os.path.basename(path)] = {
        'blender': bpy.app.version_string, 'file_version': list(bpy.data.version), 'scene': scene.name,
        'unit_scale': scene.unit_settings.scale_length,
        'objects': sorted(objects, key=lambda x: x['name']), 'materials': mats,
        'triangles': total_tris, 'min': lo, 'max': hi,
    }
json.dump(golden, open(out, 'w'), indent=1, sort_keys=True)
