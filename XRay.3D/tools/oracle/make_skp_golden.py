"""Golden values for SketchUp files (not redistributable here: public repositories' files, read from XRAY_3D_LOCAL_SAMPLES/sketchup) from an independent reader
(Python zipfile, which finds the package behind the prefix): version, container kind, thumbnail size, material and style names.

    python3 make_skp_golden.py <out.json> <file.skp> [...]
"""
import hashlib, json, os, re, struct, sys, zipfile
import xml.etree.ElementTree as ET

def png_size(b):
    return list(struct.unpack('>II', b[16:24]))

def first_png(d):
    i = d.find(b'\x89PNG\r\n\x1a\n', 0, 1 << 18)
    if i < 0: return None
    p = i + 8
    while p + 12 <= len(d):
        n, = struct.unpack('>I', d[p:p + 4]); t = d[p + 4:p + 8]; p += 12 + n
        if t == b'IEND': return d[i:p]
    return None

out = {}
for path in sys.argv[2:]:
    d = open(path, 'rb').read()
    n = d[3]; p = 4 + 2 * n; m = d[p + 3]
    version = d[p + 4:p + 4 + 2 * m].decode('utf-16le').strip('{}')
    vff = d[p + 4 + 2 * m:p + 7 + 2 * m] == b'VFF'
    e = {'version': version, 'vff': vff}
    if vff:
        z = zipfile.ZipFile(path)
        names = z.namelist()
        png = z.read('meta/model_thumbnail.png') if 'meta/model_thumbnail.png' in names else None
        mats, layers = [], []
        for nme in names:
            if nme.startswith('materials/') and nme.endswith('/material.xml'):
                for el in ET.fromstring(z.read(nme)).iter():
                    if el.tag.endswith('}material') and 'name' in el.attrib:
                        nm = el.attrib['name']
                        (layers if nm.startswith('Layer_') else mats).append(nm[6:] if nm.startswith('Layer_') else nm)
        styles = []
        for nme in names:
            if nme.startswith('styles/') and nme.endswith('/style.xml'):
                s = nme[7:-10]; s = re.sub(r'_\d+$', '', s)
                if s not in styles: styles.append(s)
        e.update(materials=[x for i, x in enumerate(mats) if x not in mats[:i]], layers=[x for i, x in enumerate(layers) if x not in layers[:i]], styles=styles, model_dat=z.getinfo('model.dat').file_size if 'model.dat' in names else None)
    else:
        png = first_png(d)
    e['thumbnail'] = {'size': png_size(png), 'bytes': len(png), 'sha256': hashlib.sha256(png).hexdigest()} if png else None
    out[os.path.basename(path)] = e
json.dump(out, open(sys.argv[1], 'w'), indent=1, sort_keys=True)
