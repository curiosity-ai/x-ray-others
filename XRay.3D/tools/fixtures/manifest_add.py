#!/usr/bin/env python3
"""Adds or refreshes entries of dataset/manifest.json for generated CC0 fixtures.

    python3 -I tools/fixtures/manifest_add.py dataset format:file [format:file ...] --source tools/fixtures/make_x.py [--title "..."] [--license SPDX]
"""
import hashlib, json, os, sys

args = sys.argv[1:]
root = args[0]
source = 'tools/fixtures/make_fixtures.py'
title = 'Hand-made fixture'
license = 'CC0-1.0'
files = []
i = 1
while i < len(args):
    if args[i] == '--source': source = args[i + 1]; i += 2
    elif args[i] == '--title': title = args[i + 1]; i += 2
    elif args[i] == '--license': license = args[i + 1]; i += 2
    else: files.append(args[i]); i += 1
path = os.path.join(root, 'manifest.json')
manifest = json.load(open(path))
entries = manifest if isinstance(manifest, list) else manifest['files']
for spec in files:
    fmt, rel = spec.split(':', 1)
    data = open(os.path.join(root, rel), 'rb').read()
    entry = {'file': rel, 'format': fmt, 'title': title, 'license': license, 'source': source, 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
    for k, e in enumerate(entries):
        if e['file'] == rel:
            entries[k] = entry
            break
    else:
        entries.append(entry)
json.dump(manifest, open(path, 'w'), indent=1)
