"""Canonical text dump of a render document.

Both sides of the parity check write the render model in this exact format, so the two dumps
can be compared line by line. The C# writer lives in tests/XRay.PS.Tests/Parity/CanonicalDump.cs
and must be kept in step with this file.

Numbers are printed with three decimals (``-0.000`` is folded to ``0.000``), byte payloads as
their length and a SHA-1 prefix, strings with every character outside printable ASCII escaped
as UTF-16 ``\\uXXXX`` units. Dataclasses are printed as ``Name(field=value,...)`` in field order.
"""

from __future__ import annotations

import dataclasses
import hashlib


def num(value) -> str:
    text = "%.3f" % float(value)
    if text == "-0.000":
        return "0.000"
    return text


def text(value: str) -> str:
    out = ['"']
    data = value.encode("utf-16-le", errors="surrogatepass")
    for i in range(0, len(data), 2):
        unit = data[i] | (data[i + 1] << 8)
        if unit == 0x22:
            out.append('\\"')
        elif unit == 0x5C:
            out.append("\\\\")
        elif 0x20 <= unit <= 0x7E:
            out.append(chr(unit))
        else:
            out.append("\\u%04x" % unit)
    out.append('"')
    return "".join(out)


def blob(value: bytes) -> str:
    return "b%d:%s" % (len(value), hashlib.sha1(bytes(value)).hexdigest()[:12])


def val(value) -> str:
    if value is None:
        return "~"
    if isinstance(value, bool):
        return "T" if value else "F"
    if isinstance(value, (int, float)):
        return num(value)
    if isinstance(value, str):
        return text(value)
    if isinstance(value, (bytes, bytearray, memoryview)):
        return blob(bytes(value))
    if isinstance(value, (list, tuple)):
        return "[" + ",".join(val(item) for item in value) + "]"
    if isinstance(value, dict):
        return "{" + ",".join(f"{val(k)}:{val(value[k])}" for k in sorted(value, key=str)) + "}"
    if dataclasses.is_dataclass(value):
        name = type(value).__name__
        if name in ("PathCommand", "TextCommand", "ImageCommand", "ClipCommand", "StateSaveCommand", "StateRestoreCommand"):
            return "<" + command(value) + ">"
        if name == "Path":
            return path(value)
        parts = []
        for field in dataclasses.fields(value):
            parts.append(f"{field.name}={val(getattr(value, field.name))}")
        return f"{name}(" + ",".join(parts) + ")"
    return "?" + type(value).__name__


def path(value) -> str:
    out = []
    for segment in value.segments:
        kind = segment.kind
        letter = {"move": "m", "line": "l", "curve": "c", "close": "h"}.get(kind, kind)
        coords = " ".join(f"{num(p.x)} {num(p.y)}" for p in segment.points)
        out.append(letter + (" " + coords if coords else ""))
    return "|".join(out)


def matrix(m) -> str:
    return " ".join(num(v) for v in (m.a, m.b, m.c, m.d, m.e, m.f))


def stroke(style) -> str:
    if style is None:
        return "~"
    return "lw=%s cap=%d join=%d ml=%s dash=%s ph=%s" % (
        num(style.line_width),
        int(style.line_cap),
        int(style.line_join),
        num(style.miter_limit),
        val(list(style.dash)),
        num(style.dash_phase),
    )


def paint(p) -> str:
    if p is None:
        return "~"
    return f"{p.kind}:{val(p.value)}"


def command(cmd) -> str:
    name = type(cmd).__name__
    if name == "PathCommand":
        return "P fr=%s op=%s fo=%s so=%s fill=%s stroke={%s} sp=%s : %s" % (
            cmd.fill_rule,
            "T" if cmd.overprint else "F",
            num(cmd.fill_opacity),
            num(cmd.stroke_opacity),
            paint(cmd.fill),
            stroke(cmd.stroke),
            paint(cmd.stroke_paint),
            path(cmd.path),
        )
    if name == "TextCommand":
        return "T %s font=%s size=%s m=[%s] fill=%s fo=%s gid=%s adj=%s" % (
            text(cmd.text),
            text(cmd.font_ref),
            num(cmd.font_size),
            matrix(cmd.matrix),
            paint(cmd.fill),
            num(cmd.fill_opacity),
            "~" if cmd.glyph_id is None else str(int(cmd.glyph_id)),
            val(cmd.pdf_text_adjustments),
        )
    if name == "ImageCommand":
        return "I %s %d %d m=[%s] mask=%s mp=%s op=%s" % (
            cmd.image_id,
            int(cmd.width),
            int(cmd.height),
            matrix(cmd.matrix),
            "T" if cmd.mask else "F",
            paint(cmd.mask_paint),
            num(cmd.opacity),
        )
    if name == "ClipCommand":
        return "C fr=%s : %s" % (cmd.fill_rule, path(cmd.path))
    if name == "StateSaveCommand":
        return "S"
    if name == "StateRestoreCommand":
        return "R"
    return "?" + name


def image_resource(res) -> str:
    return "data=%s w=%d h=%d cs=%s bpc=%d filter=%s params=%s decode=%s mask=%s pol=%s smask=%s" % (
        blob(res.data) if res.data is not None else "~",
        int(res.width),
        int(res.height),
        val(res.color_space),
        int(res.bits_per_component),
        val(res.filter),
        val(getattr(res, "filter_params", None)),
        val(getattr(res, "decode", None)),
        "T" if getattr(res, "mask", False) else "F",
        "T" if getattr(res, "mask_polarity", True) else "F",
        "~" if getattr(res, "soft_mask", None) is None else blob(res.soft_mask),
    )


def dump_document(doc) -> str:
    lines = []
    for index, page in enumerate(doc.pages, start=1):
        lines.append(f"page {index} {num(page.width)} {num(page.height)} n={len(page.commands)}")
        for cmd in page.commands:
            lines.append(command(cmd))
    res = doc.resources
    for key in sorted(res.color_spaces):
        lines.append(f"cs {key} {val(res.color_spaces[key])}")
    for key in sorted(res.functions):
        lines.append(f"fn {key} {val(res.functions[key])}")
    for key in sorted(res.patterns):
        lines.append(f"pat {key} {val(res.patterns[key])}")
    for key in sorted(res.images):
        lines.append(f"img {key} {image_resource(res.images[key])}")
    return "\n".join(lines)


def page_summaries(dump: str) -> list[dict]:
    pages = []
    for line in dump.split("\n"):
        if line.startswith("page "):
            _, index, width, height, count = line.split(" ")
            pages.append({"width": float(width), "height": float(height), "commands": int(count[2:])})
    return pages
