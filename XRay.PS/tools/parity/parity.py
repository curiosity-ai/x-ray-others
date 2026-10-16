#!/usr/bin/env python3
"""Run the C# port over the parity corpus and compare it with the Python goldens.

  parity.py run     --goldens <dir> --out <dir> --cli <psconvert> --testdata <dir> [--jobs 3] [--only s] [--skia]
  parity.py compare --goldens <dir> --out <dir> [--report report.json] [--only s] [--details]

`run` invokes `psconvert parity` for every input that has a golden summary. `compare` reports,
per input: whether the stages succeed and fail together, whether the render models are
identical (canonical dump of the model handed to the PDF writer), the PDF page count, page
sizes and extracted text, and the raster difference against both Python rasterizers.

Pixel metrics need Pillow and numpy; PDF metrics need pypdf. Both are optional.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import gzip
import io
import json
import subprocess
import sys
import time
from pathlib import Path


def _inputs(goldens: Path, only: str | None) -> list[Path]:
    items = sorted(goldens.rglob("*.json"))
    items = [p for p in items if not p.name.endswith(".report.json")]
    if only:
        items = [p for p in items if only in str(p)]
    return items


def cmd_run(args) -> int:
    goldens = args.goldens.resolve()
    out = args.out.resolve()
    items = _inputs(goldens, args.only)
    fonts = (args.testdata / "ps" / "necessary_fonts").resolve()

    def work(summary: Path) -> str:
        rel = summary.relative_to(goldens)
        name = rel.name[: -len(".json")]
        kind = rel.parts[0]
        source = args.testdata / rel.parent / name
        out_base = out / rel.parent / name
        if (out_base.parent / (name + ".json")).exists() and not args.overwrite:
            return f"skip {rel}"
        out_base.parent.mkdir(parents=True, exist_ok=True)
        cmd = [str(args.cli), "parity", kind, str(source), str(out_base), "--fonts", str(fonts), "--stages", args.stages]
        if args.skia:
            cmd.append("--skia")
        start = time.perf_counter()
        try:
            proc = subprocess.run(cmd, capture_output=True, timeout=args.timeout)
            status = "ok" if proc.returncode == 0 else f"exit {proc.returncode} {proc.stderr.decode(errors='replace')[-200:]}"
        except subprocess.TimeoutExpired:
            status = "timeout"
            (out_base.parent / (name + ".json")).write_text(json.dumps({"stages": {"load": {"ok": False, "error": "timeout"}}}))
        return f"{status} {time.perf_counter() - start:.1f}s {rel}"

    with concurrent.futures.ThreadPoolExecutor(max_workers=args.jobs) as pool:
        for line in pool.map(work, items):
            print(line, flush=True)
    return 0


def _model_section(path: Path, label: str = "## pdf 0") -> list[str] | None:
    if not path.exists():
        return None
    text = gzip.decompress(path.read_bytes()).decode("utf-8")
    lines = text.split("\n")
    out: list[str] = []
    active = False
    for line in lines:
        if line.startswith("## "):
            if active:
                break
            active = line == label
            continue
        if active:
            out.append(line)
    return out if active or out else None


def _pdf_facts(path: Path) -> dict | None:
    if not path.exists():
        return None
    try:
        import pypdf
    except ImportError:
        return None
    try:
        reader = pypdf.PdfReader(io.BytesIO(path.read_bytes()))
        pages = []
        for page in reader.pages:
            box = page.mediabox
            try:
                text = page.extract_text() or ""
            except Exception as exc:  # noqa: BLE001
                text = f"<extract failed: {exc}>"
            pages.append({"width": round(float(box.width), 3), "height": round(float(box.height), 3), "text": text})
        return {"page_count": len(reader.pages), "pages": pages}
    except Exception as exc:  # noqa: BLE001
        return {"error": str(exc)}


def _norm_text(text: str) -> str:
    return " ".join(text.split())


def _image_metrics(a: Path, b: Path) -> dict | None:
    if not a.exists() or not b.exists():
        return None
    try:
        import numpy as np
        from PIL import Image
    except ImportError:
        return None
    with Image.open(a) as ia, Image.open(b) as ib:
        ra = np.asarray(ia.convert("RGBA"), dtype=np.int16)
        rb = np.asarray(ib.convert("RGBA"), dtype=np.int16)
    if ra.shape != rb.shape:
        return {"size_match": False, "size_a": list(ra.shape[:2]), "size_b": list(rb.shape[:2])}

    # Composite over white so transparent and white backgrounds compare equal.
    def flat(x):
        alpha = x[..., 3:4] / 255.0
        return x[..., :3] * alpha + 255.0 * (1 - alpha)

    fa = flat(ra)
    fb = flat(rb)
    diff = np.abs(fa - fb).max(axis=2)
    return {
        "size_match": True,
        "mean_abs": round(float(diff.mean()), 4),
        "pct_gt_16": round(float((diff > 16).mean() * 100.0), 4),
        "pct_gt_64": round(float((diff > 64).mean() * 100.0), 4),
    }


def _compare_one(goldens: Path, out: Path, summary: Path, details: bool) -> dict:
    rel = summary.relative_to(goldens)
    name = rel.name[: -len(".json")]
    gbase = summary.parent / name
    cbase = out / rel.parent / name
    gold = json.loads(summary.read_text())
    cs_summary = out / rel.parent / (name + ".json")
    cs = json.loads(cs_summary.read_text()) if cs_summary.exists() else {"stages": {}}
    row: dict = {"file": str(rel.parent / name), "kind": rel.parts[0]}
    gst = gold.get("stages", {})
    cst = cs.get("stages", {})
    py_ok = bool(gst.get("pdf", {}).get("ok"))
    cs_ok = bool(cst.get("pdf", {}).get("ok")) if "pdf" in cst else bool(cst.get("model", {}).get("ok"))
    row["py_pdf_ok"] = py_ok
    row["cs_pdf_ok"] = cs_ok
    row["py_error"] = gst.get("pdf", {}).get("error")
    row["cs_error"] = cst.get("pdf", {}).get("error") or cst.get("model", {}).get("error") or cst.get("load", {}).get("error")
    gm = _model_section(Path(str(gbase) + ".model.txt.gz"))
    cm = _model_section(Path(str(cbase) + ".model.txt.gz"))
    if gm is not None and cm is not None:
        row["model_identical"] = gm == cm
        same = sum(1 for x, y in zip(gm, cm) if x == y)
        row["model_lines"] = (len(gm), len(cm))
        row["model_line_match"] = round(same / max(len(gm), len(cm), 1), 6)
        if gm != cm and details:
            for idx, (x, y) in enumerate(zip(gm + [""] * len(cm), cm + [""] * len(gm))):
                if x != y:
                    row["first_diff"] = {"line": idx, "py": x[:400], "cs": y[:400]}
                    break
        gp = [l for l in gm if l.startswith("page ")]
        cp = [l for l in cm if l.startswith("page ")]
        row["pages_match"] = gp == cp
    gp_facts = gold.get("pdf")
    cp_facts = _pdf_facts(Path(str(cbase) + ".pdf")) if py_ok and cs_ok else None
    if gp_facts and cp_facts and "error" not in cp_facts:
        row["pdf_pages_match"] = gp_facts["page_count"] == cp_facts["page_count"]
        row["pdf_sizes_match"] = [(p["width"], p["height"]) for p in gp_facts["pages"]] == [(p["width"], p["height"]) for p in cp_facts["pages"]]
        gt = [_norm_text(p["text"]) for p in gp_facts["pages"]]
        ct = [_norm_text(p["text"]) for p in cp_facts["pages"]]
        row["pdf_text_match"] = gt == ct
        if gt != ct and details:
            row["pdf_text_diff"] = {"py": " | ".join(gt)[:300], "cs": " | ".join(ct)[:300]}
    if row["kind"] == "ps":
        cpng = Path(str(cbase) + ".png")
        row["py_pure_ok"] = bool(gst.get("pure", {}).get("ok"))
        row["py_skia_ok"] = bool(gst.get("skia", {}).get("ok"))
        row["cs_image_ok"] = bool(cst.get("image", {}).get("ok"))
        row["vs_pure"] = _image_metrics(Path(str(gbase) + ".pure.png"), cpng)
        row["vs_skia"] = _image_metrics(Path(str(gbase) + ".skia.png"), cpng)
    else:
        row["py_skia_ok"] = bool(gst.get("skia", {}).get("ok"))
        row["cs_image_ok"] = bool(cst.get("image", {}).get("ok"))
        metrics = []
        for index in range(1, 4):
            m = _image_metrics(Path(str(gbase) + f".skia{index}.png"), Path(str(cbase) + f".{index}.png"))
            if m is not None:
                metrics.append(m)
        row["vs_skia_pages"] = metrics
    return row


def cmd_compare(args) -> int:
    goldens = args.goldens.resolve()
    out = args.out.resolve()
    rows = []
    items = _inputs(goldens, args.only)
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.jobs) as pool:
        for row in pool.map(lambda s: _compare_one(goldens, out, s, args.details), items):
            rows.append(row)
    for kind in ("ps", "xps"):
        sel = [r for r in rows if r["kind"] == kind]
        if not sel:
            continue
        both_ok = [r for r in sel if r["py_pdf_ok"] and r["cs_pdf_ok"]]
        agree = sum(1 for r in sel if r["py_pdf_ok"] == r["cs_pdf_ok"])
        modeled = [r for r in sel if "model_identical" in r and r["py_pdf_ok"]]
        print(f"== {kind}: {len(sel)} inputs")
        print(f"  success agreement: {agree}/{len(sel)} (python ok {sum(r['py_pdf_ok'] for r in sel)}, c# ok {sum(r['cs_pdf_ok'] for r in sel)})")
        if modeled:
            ident = sum(1 for r in modeled if r["model_identical"])
            pages = sum(1 for r in modeled if r.get("pages_match"))
            avg = sum(r["model_line_match"] for r in modeled) / len(modeled)
            print(f"  render model identical: {ident}/{len(modeled)}; page sizes/counts match: {pages}/{len(modeled)}; mean line match {avg:.4f}")
        if both_ok:
            for key in ("pdf_pages_match", "pdf_sizes_match", "pdf_text_match"):
                vals = [r[key] for r in both_ok if key in r]
                if vals:
                    print(f"  {key}: {sum(vals)}/{len(vals)}")
        for key in ("vs_pure", "vs_skia"):
            vals = [r[key] for r in sel if r.get(key)]
            if vals:
                sized = [v for v in vals if v.get("size_match")]
                if sized:
                    means = sorted(v["mean_abs"] for v in sized)
                    gt16 = sorted(v["pct_gt_16"] for v in sized)
                    print(f"  {key}: {len(sized)}/{len(vals)} same size; mean |diff| median {means[len(means)//2]:.3f} p90 {means[int(len(means)*0.9)]:.3f}; "
                          f"% px > 16 median {gt16[len(gt16)//2]:.3f} p90 {gt16[int(len(gt16)*0.9)]:.3f}; "
                          f"<1% px>16: {sum(1 for g in gt16 if g < 1.0)}/{len(sized)}")
        if kind == "xps":
            pages = [m for r in sel for m in r.get("vs_skia_pages", [])]
            sized = [m for m in pages if m.get("size_match")]
            if sized:
                gt16 = sorted(m["pct_gt_16"] for m in sized)
                print(f"  vs_skia pages: {len(sized)}/{len(pages)} same size; % px > 16 median {gt16[len(gt16)//2]:.3f} p90 {gt16[int(len(gt16)*0.9)]:.3f}; <1%: {sum(1 for g in gt16 if g < 1.0)}/{len(sized)}")
    if args.report:
        Path(args.report).write_text(json.dumps(rows, indent=1))
    if args.list_mismatch:
        for r in rows:
            if r["py_pdf_ok"] != r["cs_pdf_ok"] or (r["py_pdf_ok"] and r.get("model_identical") is False):
                print(json.dumps({k: r.get(k) for k in ("file", "py_pdf_ok", "cs_pdf_ok", "py_error", "cs_error", "model_line_match", "first_diff")}))
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="cmd", required=True)
    run = sub.add_parser("run")
    run.add_argument("--goldens", type=Path, required=True)
    run.add_argument("--out", type=Path, required=True)
    run.add_argument("--cli", type=Path, required=True)
    run.add_argument("--testdata", type=Path, required=True)
    run.add_argument("--jobs", type=int, default=3)
    run.add_argument("--timeout", type=int, default=900)
    run.add_argument("--only")
    run.add_argument("--overwrite", action="store_true")
    run.add_argument("--stages", default="pdf,image")
    run.add_argument("--skia", action="store_true", help="rasterize with the XRay.PS.Skia writer")
    cmp = sub.add_parser("compare")
    cmp.add_argument("--goldens", type=Path, required=True)
    cmp.add_argument("--out", type=Path, required=True)
    cmp.add_argument("--report")
    cmp.add_argument("--only")
    cmp.add_argument("--jobs", type=int, default=4)
    cmp.add_argument("--details", action="store_true")
    cmp.add_argument("--list-mismatch", action="store_true")
    args = parser.parse_args()
    return cmd_run(args) if args.cmd == "run" else cmd_compare(args)


if __name__ == "__main__":
    sys.exit(main())
