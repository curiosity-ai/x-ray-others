#!/usr/bin/env python3
"""Generate parity goldens from the original Python PostScript/XPS library.

The C# port (XRay.PS) is validated against the Python library it was ported from. This script
runs the Python implementation over every PS/EPS/XPS fixture and writes, per input file:

  <out>/<category>/<relative path>.json       summary: success, error, page sizes, PDF facts
  <out>/<category>/<relative path>.model.txt.gz  canonical render-model dump (see canonical.py)
  <out>/<category>/<relative path>.pdf        the PDF the Python library wrote
  <out>/<category>/<relative path>.skia.png   raster output, Skia backend (Python default)
  <out>/<category>/<relative path>.pure.png   raster output, pure-Python renderer (PS only)

Usage:
  python_golden.py --source <python checkout> --testdata <dir with ps/ and xps/> --out <dir>
                   [--jobs 3] [--timeout 600] [--only <substring>] [--overwrite]

The Python checkout's package is imported from <source>/src. Nothing is written into the
checkout. The pdftoppm fallback the Python XPS rasterizer can use is disabled so that both
implementations are compared on their own rendering.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import gzip
import hashlib
import importlib
import json
import os
import signal
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import canonical  # noqa: E402

PS_EXTS = {".ps", ".eps"}
XPS_EXTS = {".xps"}
PS_DIRS = ["integration", "functional", "xmp"]
XPS_DIRS = ["integration"]


def _package_root(source: Path) -> str:
    """Return the top-level package name under <source>/src that holds page/ps/document.py."""
    for child in sorted((source / "src").iterdir()):
        if (child / "page" / "ps" / "document.py").exists():
            return child.name
    raise SystemExit(f"no PostScript package found under {source / 'src'}")


class _Timeout(Exception):
    pass


def _alarm(_signum, _frame):
    raise _Timeout()


def _with_timeout(seconds: int, fn):
    signal.signal(signal.SIGALRM, _alarm)
    signal.alarm(seconds)
    try:
        return fn()
    finally:
        signal.alarm(0)


def _pdf_facts(pdf_bytes: bytes) -> dict:
    import io

    import pypdf

    reader = pypdf.PdfReader(io.BytesIO(pdf_bytes))
    pages = []
    for page in reader.pages:
        box = page.mediabox
        try:
            text = page.extract_text() or ""
        except Exception as exc:  # pragma: no cover - depends on pypdf
            text = f"<extract failed: {exc}>"
        fonts = []
        try:
            res = page.get("/Resources") or {}
            font_dict = res.get("/Font") or {}
            for key in sorted(font_dict.keys()):
                font = font_dict[key].get_object()
                fonts.append(str(font.get("/BaseFont", "")))
        except Exception:
            pass
        pages.append(
            {
                "width": round(float(box.width), 3),
                "height": round(float(box.height), 3),
                "text": text,
                "fonts": fonts,
            }
        )
    return {"page_count": len(reader.pages), "pages": pages}


def run_one(root: str, kind: str, input_path: Path, out_base: Path, fonts_dir: Path, stages: set[str], timeout: int) -> dict:
    page = importlib.import_module(f"{root}.page")
    del page
    importlib.import_module(f"{root}.page.ps")
    pdf_writer_mod = importlib.import_module(f"{root}.page.pdf.writer")
    raster_mod = importlib.import_module(f"{root}.page.image.raster_writer")
    skia_mod = importlib.import_module(f"{root}.page.image.skia_raster_writer")
    out_mod = importlib.import_module(f"{root}.page.ps.output")
    import shutil

    rasterizer_env = root.upper() + "_PAGE_RASTERIZER"
    captured: dict[str, list] = {"pdf": [], "raster": []}

    original_pdf_write = pdf_writer_mod.PdfWriter.write

    def pdf_write(self, document):
        captured["pdf"].append(canonical.dump_document(document))
        return original_pdf_write(self, document)

    pdf_writer_mod.PdfWriter.write = pdf_write

    original_raster_write = raster_mod.RenderModelRasterWriter.write
    original_skia_write = skia_mod.SkiaRasterWriter.write

    def raster_write(self, document, options):
        captured["raster"].append(canonical.dump_document(document))
        return original_raster_write(self, document, options)

    def skia_write(self, document, options):
        captured["raster"].append(canonical.dump_document(document))
        return original_skia_write(self, document, options)

    raster_mod.RenderModelRasterWriter.write = raster_write
    skia_mod.SkiaRasterWriter.write = skia_write
    # The XPS rasterizer shells out to pdftoppm for some pages when it is installed. The port
    # has no such fallback, so the golden is taken without it.
    original_which = shutil.which
    shutil.which = lambda name, *a, **k: None if name == "pdftoppm" else original_which(name, *a, **k)

    result: dict = {"file": str(input_path), "kind": kind, "stages": {}}
    out_base.parent.mkdir(parents=True, exist_ok=True)

    def flush():
        models = []
        for label in ("pdf", "raster"):
            for index, text in enumerate(captured[label]):
                models.append(f"## {label} {index}\n{text}")
        if models:
            joined = "\n".join(models).encode("utf-8")
            with gzip.open(out_base.with_suffix(out_base.suffix + ".model.txt.gz"), "wb", compresslevel=6) as fh:
                fh.write(joined)
            result["model_sha1"] = hashlib.sha1(joined).hexdigest()
        if captured["pdf"]:
            result["model_pages"] = canonical.page_summaries(captured["pdf"][0])
        out_base.with_suffix(out_base.suffix + ".json").write_text(json.dumps(result, indent=1, sort_keys=True))

    def stage(name, fn):
        start = time.perf_counter()
        # Record the attempt first: a crash inside a native extension leaves this behind.
        result["stages"][name] = {"ok": False, "error": "crashed", "seconds": 0}
        flush()
        try:
            value = _with_timeout(timeout, fn)
            result["stages"][name] = {"ok": True, "seconds": round(time.perf_counter() - start, 3)}
            flush()
            return value
        except _Timeout:
            result["stages"][name] = {"ok": False, "error": "timeout", "seconds": timeout}
        except BaseException as exc:  # noqa: BLE001 - we record every failure
            result["stages"][name] = {
                "ok": False,
                "error": f"{type(exc).__name__}: {exc}"[:500],
                "seconds": round(time.perf_counter() - start, 3),
            }
        flush()
        return None

    if kind == "ps":
        doc_mod = importlib.import_module(f"{root}.page.ps.document")
        doc = stage("load", lambda: doc_mod.PsDocument.from_file(str(input_path)))
        if doc is not None:
            dsc = doc.dsc
            result["dsc"] = None if dsc is None else {
                "bounding_box": dsc.bounding_box,
                "hires_bounding_box": dsc.hires_bounding_box,
                "title": dsc.title,
                "creator": dsc.creator,
                "orientation": dsc.orientation,
            }
            result["is_eps"] = doc.is_eps
            result["page_parts"] = len(doc.pages)
            if "pdf" in stages:
                pdf = stage("pdf", lambda: doc.to_pdf(out_mod.PdfSaveOptions(additional_fonts_folder=str(fonts_dir))))
                if pdf is not None:
                    out_base.with_suffix(out_base.suffix + ".pdf").write_bytes(pdf)
                    facts = stage("pdf_facts", lambda: _pdf_facts(pdf))
                    if facts is not None:
                        result["pdf"] = facts
            if "pure" in stages:
                os.environ[rasterizer_env] = "python"
                png = stage("pure", lambda: doc.to_image(out_mod.ImageSaveOptions(format="png", dpi=96, additional_fonts_folder=str(fonts_dir))))
                os.environ.pop(rasterizer_env, None)
                if png is not None:
                    out_base.with_suffix(out_base.suffix + ".pure.png").write_bytes(png)
            if "skia" in stages:
                os.environ.pop(rasterizer_env, None)
                png = stage("skia", lambda: doc.to_image(out_mod.ImageSaveOptions(format="png", dpi=96, additional_fonts_folder=str(fonts_dir))))
                if png is not None:
                    out_base.with_suffix(out_base.suffix + ".skia.png").write_bytes(png)
    else:
        doc_mod = importlib.import_module(f"{root}.page.xps.document")
        doc = stage("load", lambda: doc_mod.XpsDocument.from_file(str(input_path)))
        if doc is not None:
            result["page_parts"] = len(doc.builder.pages)
            if "pdf" in stages:
                pdf = stage("pdf", lambda: doc.to_pdf())
                if pdf is not None:
                    out_base.with_suffix(out_base.suffix + ".pdf").write_bytes(pdf)
                    facts = stage("pdf_facts", lambda: _pdf_facts(pdf))
                    if facts is not None:
                        result["pdf"] = facts
            if "skia" in stages:
                pages = stage("skia", lambda: doc.to_images(out_mod.ImageSaveOptions(format="png", dpi=96), max_pages=3))
                if pages is not None:
                    result["raster_pages"] = len(pages)
                    for index, png in enumerate(pages, start=1):
                        out_base.with_suffix(out_base.suffix + f".skia{index}.png").write_bytes(png)

    flush()
    return result


def _collect(testdata: Path, only: str | None) -> list[tuple[str, Path, Path]]:
    items: list[tuple[str, Path, Path]] = []
    for sub in PS_DIRS:
        base = testdata / "ps" / sub
        if not base.exists():
            continue
        for path in sorted(base.rglob("*")):
            if path.is_file() and path.suffix.lower() in PS_EXTS:
                items.append(("ps", path, Path("ps") / path.relative_to(testdata / "ps")))
    for sub in XPS_DIRS:
        base = testdata / "xps" / sub
        if not base.exists():
            continue
        for path in sorted(base.rglob("*")):
            if path.is_file() and path.suffix.lower() in XPS_EXTS:
                items.append(("xps", path, Path("xps") / path.relative_to(testdata / "xps")))
    if only:
        items = [item for item in items if only in str(item[1])]
    return items


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--testdata", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--jobs", type=int, default=3)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--only")
    parser.add_argument("--stages", default="pdf,skia,pure")
    parser.add_argument("--overwrite", action="store_true")
    parser.add_argument("--one", nargs=3, metavar=("KIND", "INPUT", "OUTBASE"))
    args = parser.parse_args()
    source = args.source.resolve()
    sys.path.insert(0, str(source / "src"))
    root = _package_root(source)
    fonts_dir = (args.testdata / "ps" / "necessary_fonts").resolve()
    stages = set(args.stages.split(","))

    if args.one:
        kind, input_path, out_base = args.one
        run_one(root, kind, Path(input_path), Path(out_base), fonts_dir, stages, args.timeout)
        return 0

    items = _collect(args.testdata.resolve(), args.only)
    print(f"{len(items)} inputs", flush=True)

    def worker(item):
        kind, path, rel = item
        out_base = args.out / rel
        summary = out_base.with_suffix(out_base.suffix + ".json")
        if summary.exists() and not args.overwrite:
            return f"skip {rel}"
        cmd = [sys.executable, "-I", str(Path(__file__).resolve()), "--source", str(source), "--testdata",
               str(args.testdata.resolve()), "--out", str(args.out.resolve()), "--timeout", str(args.timeout),
               "--stages", args.stages, "--one", kind, str(path), str(out_base.resolve())]
        start = time.perf_counter()
        try:
            proc = subprocess.run(cmd, capture_output=True, timeout=args.timeout * 4 + 60)
            status = "ok" if proc.returncode == 0 else f"exit {proc.returncode}: {proc.stderr.decode(errors='replace')[-300:]}"
        except subprocess.TimeoutExpired:
            status = "hard timeout"
            out_base.parent.mkdir(parents=True, exist_ok=True)
            summary.write_text(json.dumps({"file": str(path), "kind": kind, "stages": {"load": {"ok": False, "error": "hard timeout"}}}))
        return f"{status} {time.perf_counter() - start:.1f}s {rel}"

    with concurrent.futures.ThreadPoolExecutor(max_workers=args.jobs) as pool:
        for line in pool.map(worker, items):
            print(line, flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
