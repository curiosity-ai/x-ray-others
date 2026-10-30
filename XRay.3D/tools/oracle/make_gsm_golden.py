"""Independent values for the two Archicad library parts of the dataset: the lamp is a polyhedron listed as VERT / EDGE / PGON in the 3D script, so a separate
Python parse of the script gives its triangle count, area, volume and bounds; the table is built from a cylinder, blocks and a rounded-rectangle prism, so its
area and volume come from closed forms (with the polygon approximation of the circles at RESOL 36).

    python3 make_gsm_golden.py <dataset/pln> <out.json>
"""
import json, math, os, re, struct, sys

def sections(d):
    off, size = struct.unpack('<II', d[0x34:0x3c])
    out = {}
    for p in range(off, off + size, 16):
        tag = d[p:p + 4][::-1].decode(); o, s, _ = struct.unpack('<III', d[p + 4:p + 16])
        out[tag] = d[o + 16:o + s]
    return out

def script(sec, tag):
    return sec[tag].decode('utf-8').lstrip('﻿') if tag in sec else ''

def params(pard):
    r = {}
    pos = 81
    while pos + 40 <= len(pard):
        name = pard[pos + 8:pos + 40].split(b'\0')[0]
        if not name: break
        r[name.decode()] = struct.unpack('<d', pard[pos:pos + 8])[0]; pos += 72
    return r

def lamp(d):
    sec = sections(d); p = params(sec['PARD'])
    text = script(sec, '3DSC')
    verts, edges, polys = [], [], []
    scale = (p['A'] / 0.3, p['B'] / 0.3, p['ZZYZX'] / 0.445); shift = (0.15, 0.15, 0)
    for line in text.splitlines():
        line = line.split('!')[0].strip()
        m = re.match(r'(\w+)\s*(.*)', line)
        if not m: continue
        w, a = m.group(1), m.group(2)
        nums = [float(x) for x in re.findall(r'-?\d+\.?\d*(?:e-?\d+)?', a)] if w in ('VERT', 'EDGE', 'PGON') else []
        if w == 'BASE': verts, edges = [], []
        elif w == 'VERT': verts.append(tuple((nums[i] + shift[i]) * scale[i] if False else (nums[i] * scale[i] + shift[i] * scale[i]) for i in range(3)))
        elif w == 'EDGE': edges.append((int(nums[0]), int(nums[1])))
        elif w == 'PGON':
            n = int(nums[0]); ring = []
            for e in nums[3:3 + n]:
                e = int(e); a0, a1 = edges[abs(e) - 1]; ring.append(verts[(a0 if e > 0 else a1) - 1])
            polys.append(ring)
    tris = 0; area = 0.0; vol = 0.0
    lo = [1e9] * 3; hi = [-1e9] * 3
    for ring in polys:
        for i in range(1, len(ring) - 1):
            a, b, c = ring[0], ring[i], ring[i + 1]
            u = [b[k] - a[k] for k in range(3)]; v = [c[k] - a[k] for k in range(3)]
            n = [u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0]]
            area += 0.5 * math.sqrt(sum(x * x for x in n))
            vol += (a[0] * (b[1] * c[2] - b[2] * c[1]) - a[1] * (b[0] * c[2] - b[2] * c[0]) + a[2] * (b[0] * c[1] - b[1] * c[0])) / 6
            tris += 1
        for q in ring:
            for k in range(3): lo[k] = min(lo[k], q[k]); hi[k] = max(hi[k], q[k])
    return {'parameters': p, 'triangles': tris, 'area': area, 'volume': vol, 'min': lo, 'max': hi, 'materials': len(re.findall(r'DEFINE MATERIAL', script(sec, '1DSC')))}

def table(d):
    sec = sections(d); p = params(sec['PARD'])
    A, B, Z = p['A'], p['B'], p['ZZYZX']
    t, cr, lr, li, ah = p['topThickness'], p['cornerRadius'], p['legRadius'], p['legInset'], p['apronHeight']
    r = min(cr, min(A, B) / 2 - 0.001)
    inset = li + lr; legh = Z - t
    n = 36; ngon = 0.5 * n * math.sin(2 * math.pi / n)
    legs = 4 * ngon * lr * lr * legh
    thk = 0.02
    apron = 2 * (A - 2 * inset) * thk * ah + 2 * (B - 2 * inset) * thk * ah
    # the rounded rectangle: four straight sides and four arcs of a quarter circle drawn with RESOL / 4 chords
    k = n // 4
    arc = 0.5 * r * r * k * math.sin(2 * math.pi / n) * 1.0
    chord_corner = (A - 2 * r) * (B - 2 * r) + 2 * r * (A - 2 * r) + 2 * r * (B - 2 * r) + 4 * (0.5 * r * r * k * math.sin(2 * math.pi / n))
    top = chord_corner * t
    return {'parameters': p, 'volume': legs + apron + top, 'min': [0, 0, 0], 'max': [A, B, Z], 'materials': len(re.findall(r'DEFINE MATERIAL', script(sec, '1DSC')))}

root = sys.argv[1]
out = {}
out['table-lamp.gsm'] = lamp(open(os.path.join(root, 'table-lamp.gsm'), 'rb').read())
out['parametric-table.gsm'] = table(open(os.path.join(root, 'parametric-table.gsm'), 'rb').read())
json.dump(out, open(sys.argv[2], 'w'), indent=1, sort_keys=True)
