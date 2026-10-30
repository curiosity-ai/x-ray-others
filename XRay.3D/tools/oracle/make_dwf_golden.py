#!/usr/bin/env python3
"""Expected values for the DWF tests from a second, independent parse of the W2D text streams (python zipfile and regular expressions).

    python3 -I tools/oracle/make_dwf_golden.py dataset/dwf > tests/XRay.ThreeD.Tests/Corpus/dwf-golden.json

Per file: paper size and units, the descriptor's scale, the number of polylines and their points, the distinct colours, the texts (count,
first and last string, font heights) and the extent of every polyline point after the scale.
"""
import glob, json, os, re, sys, zipfile

out = {}
for path in sorted(p for d in sys.argv[1:] for p in glob.glob(os.path.join(d, '*.dwf'))):
    z = zipfile.ZipFile(path)
    desc = z.read([n for n in z.namelist() if n.endswith('descriptor.xml')][0]).decode()
    paper = re.search(r'<ePlot:Paper units="(\w+)" width="([\d.]+)" height="([\d.]+)"', desc)
    scale = float(re.search(r'transform="([\d.eE+-]+) ', desc).group(1))
    w2d = z.read([n for n in z.namelist() if n.endswith('.w2d')][0]).decode('latin-1')
    polylines = points = 0
    colours = set()
    texts = []
    heights = set()
    xs, ys = [], []
    for line in w2d.split('\n'):
        line = line.strip()
        if line.startswith('(Color'):
            colours.add(tuple(int(v) for v in re.findall(r'\d+', line)[:3]))
        elif line.startswith('(Font'):
            heights.add(int(re.search(r'\(Height (\d+)\)', line).group(1)))
        elif line.startswith('(Text'):
            texts.append(re.search(r"'(.*)'", line).group(1))
        elif line.startswith('P '):
            parts = line.split()
            n = int(parts[1])
            assert n == len(parts) - 2
            polylines += 1
            points += n
            for pt in parts[2:]:
                x, y = pt.split(',')
                xs.append(float(x) * scale)
                ys.append(float(y) * scale)
    out[os.path.basename(path)] = {
        'units': paper.group(1), 'width': float(paper.group(2)), 'height': float(paper.group(3)), 'scale': scale,
        'polylines': polylines, 'points': points, 'colours': len(colours), 'texts': len(texts),
        'firstText': texts[0] if texts else None, 'lastText': texts[-1] if texts else None, 'fontHeights': sorted(heights),
        'min': [round(min(xs), 4), round(min(ys), 4)], 'max': [round(max(xs), 4), round(max(ys), 4)],
    }
print(json.dumps(out, indent=1))
