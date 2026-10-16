#!/usr/bin/env python3
"""Regenerate the parity goldens of XRay.OneNote from the Python reference implementation.

The C# package is a port of an MIT-licensed open-source Python OneNote reader (see ../LICENSE).
This script imports that reader from a local checkout, loads every fixture, and writes:

  tests/XRay.OneNote.Tests/Goldens/dom/<fixture>.json   deterministic DOM dump (same schema as
                                                        tools/Shared/DomJson.cs), with page history
  tests/XRay.OneNote.Tests/Goldens/pdf/<fixture>.json   semantic manifest of the reference PDF
                                                        export (page count, text, links, images)

It can also compare PDFs written by the C# port against those manifests with the same pypdf-based
extraction (--compare-pdfs DIR, where DIR holds <fixture>.pdf files from `onenotetool pdf`).

Requirements: Python 3.10+, `pip install reportlab pypdf`.

Usage:
  python3 tools/regenerate_goldens.py --source /path/to/python/checkout [--fixtures DIR]
  python3 tools/regenerate_goldens.py --source ... --compare-pdfs test-output/pdf
"""

from __future__ import annotations

import argparse
import glob
import hashlib
import importlib
import io
import json
import os
import re
import sys
from datetime import timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent
PACKAGE_ROOT = HERE.parent
GOLDENS = PACKAGE_ROOT / "tests" / "XRay.OneNote.Tests" / "Goldens"


def find_source(explicit: str | None) -> Path:
    if explicit:
        return Path(explicit).resolve()
    env = os.environ.get("XRAY_ONENOTE_PY_SOURCE")
    if env:
        return Path(env).resolve()
    for candidate in sorted(glob.glob(str(PACKAGE_ROOT.parent.parent / "*Note-FOSS-for-Python"))):
        return Path(candidate)
    sys.exit("Python checkout not found: pass --source or set XRAY_ONENOTE_PY_SOURCE")


def import_reference(source: Path):
    """Import the reference package without naming it: it is the one package under src/ with note/."""
    src = source / "src"
    names = [p.name for p in src.iterdir() if (p / "note" / "__init__.py").exists()]
    if len(names) != 1:
        sys.exit(f"Could not identify the reference package under {src}")
    sys.dont_write_bytecode = True  # leave the reference checkout untouched
    sys.path.insert(0, str(src))
    return importlib.import_module(f"{names[0]}.note")


def time(value):
    return None if value is None else value.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def name(value):
    return None if value is None else value.name


def paragraph_style(style):
    return {
        "font_name": style.FontName,
        "font_size": style.FontSize,
        "font_color": style.FontColor,
        "highlight": style.Highlight,
        "bold": style.IsBold,
        "italic": style.IsItalic,
        "underline": style.IsUnderline,
        "strikethrough": style.IsStrikethrough,
        "superscript": style.IsSuperscript,
        "subscript": style.IsSubscript,
    }


def text_style(style):
    result = paragraph_style(style)
    result.update(
        {
            "is_hyperlink": style.IsHyperlink,
            "hyperlink_address": style.HyperlinkAddress,
            "language": style.Language,
        }
    )
    return result


def tags(items):
    return [
        {
            "label": tag.Label,
            "icon": tag.Icon,
            "status": tag.Status.name,
            "font_color": tag.FontColor,
            "highlight": tag.Highlight,
            "creation_time": time(tag.CreationTime),
            "completed_time": time(tag.CompletedTime),
        }
        for tag in items
    ]


def sha256(data) -> str:
    return hashlib.sha256(bytes(data)).hexdigest()


def dump_node(node):
    if node is None:
        return None
    kind = type(node).__name__
    children = lambda: [dump_node(child) for child in node]  # noqa: E731
    if kind == "Page":
        return {
            "type": "Page",
            "author": node.Author,
            "creation_time": time(node.CreationTime),
            "last_modified_time": time(node.LastModifiedTime),
            "level": node.Level,
            "has_title": node.Title is not None,
            "children": children(),
        }
    if kind == "Title":
        return {
            "type": "Title",
            "text": dump_node(node.TitleText),
            "date": dump_node(node.TitleDate),
            "time": dump_node(node.TitleTime),
        }
    if kind == "Outline":
        return {
            "type": "Outline",
            "horizontal_offset": node.HorizontalOffset,
            "vertical_offset": node.VerticalOffset,
            "max_width": node.MaxWidth,
            "children": children(),
        }
    if kind == "OutlineElement":
        number_list = node.NumberList
        return {
            "type": "OutlineElement",
            "number_list": None
            if number_list is None
            else {"format": number_list.Format, "number_format": number_list.NumberFormat, "restart": number_list.Restart},
            "children": children(),
        }
    if kind == "RichText":
        return {
            "type": "RichText",
            "text": node.Text,
            "alignment": name(node.Alignment),
            "paragraph_style": paragraph_style(node.ParagraphStyle),
            "runs": [{"text": run.Text, "style": text_style(run.Style)} for run in node.TextRuns],
            "tags": tags(node.Tags),
        }
    if kind == "Image":
        return {
            "type": "Image",
            "file_name": node.FileName,
            "bytes_len": len(node.Bytes),
            "sha256": sha256(node.Bytes),
            "width": node.Width,
            "height": node.Height,
            "original_width": node.OriginalWidth,
            "original_height": node.OriginalHeight,
            "alignment": name(node.Alignment),
            "alt_text": node.AlternativeTextDescription,
            "hyperlink_url": node.HyperlinkUrl,
            "tags": tags(node.Tags),
            "children": children(),
        }
    if kind == "AttachedFile":
        return {
            "type": "AttachedFile",
            "file_name": node.FileName,
            "bytes_len": len(node.Bytes),
            "sha256": sha256(node.Bytes),
            "tags": tags(node.Tags),
        }
    if kind == "Table":
        return {
            "type": "Table",
            "columns": [column.Width for column in node.Columns],
            "borders_visible": node.IsBordersVisible,
            "tags": tags(node.Tags),
            "children": children(),
        }
    if kind in ("TableRow", "TableCell"):
        return {"type": kind, "children": children()}
    return {"type": kind}


def dump_document(note, path: Path) -> dict:
    document = note.Document(str(path))
    history_document = note.Document(str(path), note.LoadOptions(LoadHistory=True))
    histories = []
    for page in history_document.GetChildNodes(note.Page):
        history = history_document.GetPageHistory(page)
        histories.append(
            {
                "current_is_page": history.Current is page,
                "count": len(history),
                "versions": [dump_node(version) for version in history],
            }
        )
    return {
        "fixture": path.name,
        "display_name": document.DisplayName,
        "file_format": document.FileFormat.name,
        "pages": [dump_node(page) for page in document],
        "history": histories,
    }


def normalize_text(text: str) -> str:
    lines = []
    for line in text.replace("\xa0", " ").splitlines():
        compact = re.sub(r"\s+", " ", line).strip()
        if compact:
            lines.append(compact)
    return "\n".join(lines)


def pdf_manifest(pdf_bytes: bytes) -> dict:
    from pypdf import PdfReader

    reader = PdfReader(io.BytesIO(pdf_bytes))
    pages = []
    for index, page in enumerate(reader.pages):
        links = set()
        for annotation in page.get("/Annots") or []:
            action = annotation.get_object().get("/A")
            if action is not None and isinstance(action.get("/URI"), str):
                links.add(action.get("/URI").strip())
        images = 0
        resources = page.get("/Resources")
        x_objects = resources.get("/XObject") if resources else None
        if x_objects is not None:
            for _, obj in x_objects.get_object().items():
                if obj.get_object().get("/Subtype") == "/Image":
                    images += 1
        pages.append(
            {
                "index": index,
                "text": normalize_text(page.extract_text() or ""),
                "links": sorted(links),
                "image_count": images,
            }
        )
    return {"page_count": len(pages), "pages": pages}


def write_json(path: Path, value) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=1, sort_keys=True, ensure_ascii=False) + "\n", encoding="utf-8")


def squash(text: str) -> str:
    return re.sub(r"\s+", "", text)


def compare_pdfs(directory: Path) -> int:
    failures = 0
    for golden in sorted((GOLDENS / "pdf").glob("*.json")):
        expected = json.loads(golden.read_text(encoding="utf-8"))
        candidate = directory / f"{golden.stem}.pdf"
        if not candidate.exists():
            print(f"MISSING  {golden.stem}: {candidate} not found")
            failures += 1
            continue
        actual = pdf_manifest(candidate.read_bytes())
        exact = actual == expected
        same_shape = actual["page_count"] == expected["page_count"] and all(
            a["links"] == e["links"] and a["image_count"] == e["image_count"] and squash(a["text"]) == squash(e["text"])
            for a, e in zip(actual["pages"], expected["pages"])
        )
        status = "EXACT" if exact else ("MATCH" if same_shape else "DIFF")
        print(f"{status:8} {golden.stem}: pages {actual['page_count']}/{expected['page_count']}")
        if not same_shape:
            failures += 1
            for a, e in zip(actual["pages"], expected["pages"]):
                if a != e:
                    print(f"   page {a['index']}\n     expected: {e}\n     actual:   {a}")
    return failures


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", help="path of the Python checkout (default: $XRAY_ONENOTE_PY_SOURCE or a sibling *Note-FOSS-for-Python)")
    parser.add_argument("--fixtures", help="directory of .one files (default: <source>/testfiles)")
    parser.add_argument("--compare-pdfs", metavar="DIR", help="compare C# PDFs in DIR against the PDF goldens instead of regenerating")
    args = parser.parse_args()

    source = find_source(args.source)
    note = import_reference(source)

    if args.compare_pdfs:
        return 1 if compare_pdfs(Path(args.compare_pdfs)) else 0

    fixtures = Path(args.fixtures) if args.fixtures else source / "testfiles"
    for path in sorted(fixtures.glob("*.one")):
        write_json(GOLDENS / "dom" / f"{path.stem}.json", dump_document(note, path))
        buffer = io.BytesIO()
        note.Document(str(path)).Save(buffer, note.SaveFormat.Pdf)
        write_json(GOLDENS / "pdf" / f"{path.stem}.json", pdf_manifest(buffer.getvalue()))
        print(f"wrote goldens for {path.name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
