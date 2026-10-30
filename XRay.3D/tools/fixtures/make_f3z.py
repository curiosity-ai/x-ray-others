#!/usr/bin/env python3
"""Writes dataset/synthetic/design-description.f3z and part-docstruct.f3d (CC0): a Fusion multi-document archive around the synthetic part (the member
is stored whole), and a small part package written to the Manifest.dat grammar of the public description (current generation header, capability registry, asset
tail with a display name, a Properties.dat docstruct, a configuration table, deflated entries).

    python3 tools/fixtures/make_f3z.py dataset
"""
import json, os, struct, sys, zipfile

root = sys.argv[1]


def lp8(s):
    b = s.encode()
    return struct.pack('<I', len(b)) + b


def lp16(s):
    return struct.pack('<I', len(s)) + s.encode('utf-16-le')


g = lambda n: '%08x-0000-4000-8000-%012x' % (n, n)
manifest = lp8('3-2-0-0') + lp8('FusionDocType') + lp16('.f3d') + lp16('Bracket') + lp16('A bracket') + lp16(g(1)) + lp16(g(2))
manifest += struct.pack('<IIIII', 1234, 20, 7, 99, 2) + lp8('Application') + struct.pack('<I', 1) + lp8('CAM') + struct.pack('<I', 4)
manifest += struct.pack('<I', 0x2712) + b'\0' * 8
manifest += lp16(g(3)) + struct.pack('<I', 1) + lp16('Design') + struct.pack('<I', 0) + b'\x00' + lp16('Bracket shown') + lp16('urn:adsk.wipprod:dm.lineage:ABCDEF')
asset = lp16('Design') + lp16(g(4)) + lp16(g(5)) + lp8('FusionAssetType') + struct.pack('<I', 20) + struct.pack('<I', 2) + lp8('Application') + struct.pack('<I', 1) + lp8('Server') + struct.pack('<I', 6)
docstruct = json.dumps({'docstruct': {'version': '1.0', 'type': 'part-design', 'subtype': 'part-sheetmetal', 'attributes': {}}}).encode()
table = json.dumps({'active': 'Small', 'configurations': {'Small': {'parameters': {'width': '20 mm'}, 'suppressed': [], 'material': 'Steel'}, 'Large': {'parameters': {'width': '40 mm'}, 'suppressed': ['Fillet1'], 'material': 'Steel'}}}).encode()
with zipfile.ZipFile(os.path.join(root, 'synthetic', 'part-docstruct.f3d'), 'w', zipfile.ZIP_DEFLATED) as z:
    z.writestr('Manifest.dat', manifest)
    z.writestr('Properties.dat', struct.pack('<I', len(docstruct)) + docstruct)
    z.writestr('Design[Active]/Manifest.dat', asset)
    z.writestr('Design[Active]/DesignConfigurationTable.BlobParts/DesignConfigurationTable.%s.dsgcfg' % g(6), table)
    z.writestr('Design[Active]/Breps.BlobParts/BREP.%s.smb' % g(7), bytes(range(200)) * 5)

f3d = open(os.path.join(root, 'synthetic', 'part-docstruct.f3d'), 'rb').read()
with zipfile.ZipFile(os.path.join(root, 'synthetic', 'design-description.f3z'), 'w', zipfile.ZIP_STORED) as z:
    z.writestr('Manifest.json', json.dumps({'root': 'Controller.f3d'}))
    z.writestr('DesignDescription.json', json.dumps({'name': 'Autodesk Design Description', 'version': '0.1', 'designDescription': {
        'id': 'a1', 'name': 'Controller assembly', 'currentVersion': 1, 'designGraphs': [{'creationDate': '2025-06-01T10:20:30Z', 'creatingService': 'Fusion', 'rootIds': [1], 'designObjects': [{'id': 1, 'friendlyName': 'Controller'}, {'id': 2, 'friendlyName': 'Enclosure'}]}]}}))
    z.writestr('Controller.f3d', f3d)
