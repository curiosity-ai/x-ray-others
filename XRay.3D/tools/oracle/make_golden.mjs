// Writes the expected values the SolidWorks tests compare against, from an independent reader.
//
//   node make_golden.mjs <engine-dir> <dataset/> > tests/XRay.ThreeD.Tests/Corpus/solidworks-golden.json
//
// <engine-dir> holds the reference reader (a WebAssembly build: sw_core.mjs and sw_core_bg.wasm). It is not part of
// this repository; the committed golden file is what the tests use. Values: stream and component counts, drawn
// vertex/triangle/face counts, world bounds and coordinate sums of the decoded mesh.
import fs from 'fs';
import path from 'path';
const [engineDir, datasetDir] = process.argv.slice(2);
const sw = await import(path.resolve(engineDir, 'sw_core.mjs'));
await sw.default({ module_or_path: fs.readFileSync(path.join(engineDir, 'sw_core_bg.wasm')) });
const out = {};
for (const ext of ['sldprt', 'sldasm', 'slddrw']) {
  const dir = path.join(datasetDir, ext);
  if (!fs.existsSync(dir)) continue;
  for (const f of fs.readdirSync(dir).sort()) {
    const doc = sw.SwDocument.open(new Uint8Array(fs.readFileSync(path.join(dir, f))));
    const info = JSON.parse(doc.info_json());
    const e = { kind: info.kind, streams: info.streamNames.length, componentFiles: JSON.parse(doc.component_files_json()), sheets: info.sheetNames };
    const m = doc.extract_mesh();
    if (m) {
      const p = m.take_positions(), i = m.take_indices();
      const min = [1e30, 1e30, 1e30], max = [-1e30, -1e30, -1e30], sum = [0, 0, 0];
      for (let k = 0; k < p.length; k++) { const a = k % 3; min[a] = Math.min(min[a], p[k]); max[a] = Math.max(max[a], p[k]); sum[a] += p[k]; }
      const cols = m.take_face_colors(), hist = {};
      for (const c of cols) { const k = c === 0xffffffff ? 'none' : c.toString(16).padStart(6, '0'); hist[k] = (hist[k] || 0) + 1; }
      Object.assign(e, { faceColors: hist, faces: m.face_count(), vertices: p.length / 3, triangles: i.length / 3, instances: JSON.parse(m.parts_json()).length, min, max, sum });
    }
    out[f] = e;
  }
}
console.log(JSON.stringify(out, null, 1));
