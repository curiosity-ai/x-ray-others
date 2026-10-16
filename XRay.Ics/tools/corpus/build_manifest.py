#!/usr/bin/env python3
"""Builds tests/XRay.Ics.Tests/Corpus/ics-manifest.json: the expected fields of every .ics fixture, read
by independent implementations (python `icalendar` for parsing, `recurring_ical_events` for
recurrence expansion) so X-Ray.Ics is checked against a second reader, not against itself.

    python3 -m venv /tmp/venv && /tmp/venv/bin/pip install icalendar==7.3.0 recurring-ical-events==3.8.2
    /tmp/venv/bin/python -I build_manifest.py <test_documents> <out.json>

Where the library is deliberately different from the oracle (or the oracle cannot read the file at
all), OVERRIDES holds the expected value and the reason. Read the reason before changing one.
"""
import base64
import re
import datetime
import json
import os
import sys
import copy
import quopri
import urllib.parse

import icalendar
import recurring_ical_events
from icalendar import Calendar

SOURCES = ["ics/xray-ics", "ics/icalendar-py", "ics/ical-net"]
EXPANSION_COUNT = 12
# TZIDs the oracle resolves by guessing (its own name matching); X-Ray.Ics only resolves what it can prove.
UNRESOLVED_TZIDS = {"Western/Central Europe"}
DEFINED_TZIDS = set()  # TZIDs the current file defines with a VTIMEZONE (those the library resolves)


def dt_str(value):
    """date -> YYYY-MM-DD, aware datetime -> UTC with Z, naive datetime -> no suffix."""
    if value is None:
        return None
    if isinstance(value, datetime.datetime):
        if value.tzinfo is not None and value.utcoffset() is not None:
            return value.astimezone(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        return value.strftime("%Y-%m-%dT%H:%M:%S")
    if isinstance(value, datetime.date):
        return value.strftime("%Y-%m-%d")
    return str(value)


def as_list(value):
    if value is None:
        return []
    return value if isinstance(value, list) else [value]


def strip_mailto(value):
    value = str(value).strip()
    if value.lower().startswith("mailto:"):
        value = value[7:]
        value = value.split("?")[0]
    return value.strip()


def person(prop, attendee):
    value = str(prop).strip()
    params = prop.params if hasattr(prop, "params") else {}
    email = strip_mailto(value) if value.lower().startswith("mailto:") else None
    if email is None:
        embedded = params.get("EMAIL")
        email = strip_mailto(embedded) if embedded else None
    if email is None and "@" in value and ":" not in value and "/" not in value and " " not in value:
        email = value
    result = {"email": email, "address": email or strip_mailto(value), "cn": params.get("CN") or None}
    if chr(92) in value:
        result["address_unreliable"] = True
    if attendee:
        result["role"] = str(params.get("ROLE", "REQ-PARTICIPANT")).upper()
        result["partstat"] = str(params.get("PARTSTAT", "NEEDS-ACTION")).upper()
        rsvp = params.get("RSVP")
        result["rsvp"] = None if rsvp is None else str(rsvp).upper() in ("TRUE", "YES")
        result["cutype"] = str(params.get("CUTYPE", "INDIVIDUAL")).upper()
    return result


def raw_binary_sizes(data):
    """Decoded sizes of every inline-base64 ATTACH line, from the raw text (independent of icalendar's vBinary)."""
    text = decode_bytes(data).decode("utf-8")
    text = text.replace("\r\n", "\n").replace("\r", "\n")
    text = re.sub(r"\n[ \t]", "", text)
    sizes = []
    for line in text.split("\n"):
        if line.upper().startswith("ATTACH") and ("ENCODING=BASE64" in line.upper() or "VALUE=BINARY" in line.upper()):
            payload = re.sub(r"[^A-Za-z0-9+/]", "", line.split(":", 1)[1]) if ":" in line else ""
            payload = payload[: len(payload) - (len(payload) % 4 == 1)]
            payload += "=" * (-len(payload) % 4)
            try:
                sizes.append(len(base64.b64decode(payload)))
            except Exception:
                sizes.append(0)
    return sizes


def attachment(prop):
    params = prop.params if hasattr(prop, "params") else {}
    name = params.get("FILENAME") or params.get("X-FILENAME") or params.get("X-APPLE-FILENAME") or params.get("NAME")
    entry = {"name": name, "content_type": params.get("FMTTYPE"), "size": None, "uri": None}
    if isinstance(prop, icalendar.prop.vBinary):
        payload = prop.obj if isinstance(prop.obj, str) else prop.obj.decode("ascii")
        entry["size"] = len(base64.b64decode(payload + "=" * (-len(payload) % 4))) if payload else 0
    else:
        text = str(prop)
        if text.lower().startswith("data:") and ";base64," in text.lower():
            payload = text.split(",", 1)[1]
            entry["size"] = len(base64.b64decode(payload + "=" * (-len(payload) % 4)))
            entry["inline_data_uri"] = True
        else:
            entry["uri"] = text
            if name is None:
                path = text.split("?")[0].split("#")[0]
                last = path.rsplit("/", 1)[-1]
                entry["name"] = urllib.parse.unquote(last) if last and ":" not in last else None
    return entry


def event_entry(ev):
    summary = ev.get("summary")
    description = ev.get("description")
    location = ev.get("location")
    categories = []
    for c in as_list(ev.get("categories")):
        categories.extend(str(x) for x in getattr(c, "cats", [c]))
    organizer = ev.get("organizer")
    start = ev.get("dtstart")
    try:
        end = ev.end
    except Exception:
        end = None
    if start is not None and str(start.params.get("VALUE", "")).upper() == "DATE" and isinstance(start.dt, datetime.datetime):  # noqa
        start_dt = start.dt.date()
        end = end.date() if isinstance(end, datetime.datetime) else end
    else:
        start_dt = start.dt if start is not None else None
    if start is not None and str(start.params.get("TZID", "")) in UNRESOLVED_TZIDS - DEFINED_TZIDS and isinstance(start_dt, datetime.datetime):
        start_dt = start_dt.replace(tzinfo=None)
        end = end.replace(tzinfo=None) if isinstance(end, datetime.datetime) else end
    entry = {
        "uid": str(ev.get("uid")) if ev.get("uid") is not None else None,
        "summary": str(summary) if summary is not None else None,
        "description": str(description) if description is not None else None,
        "location": str(location) if location is not None else None,
        "start": dt_str(start_dt),
        "end": dt_str(end) if start is not None else None,
        "created": dt_str(ev.get("created").dt) if ev.get("created") is not None else None,
        "stamp": dt_str(ev.get("dtstamp").dt) if ev.get("dtstamp") is not None else None,
        "status": str(ev.get("status")).upper() if ev.get("status") is not None else None,
        "organizer": person(organizer, False) if organizer is not None else None,
        "attendees": [person(a, True) for a in as_list(ev.get("attendee"))],
        "attachments": [attachment(a) for a in as_list(ev.get("attach"))],
        "categories": categories,
        "recurring": ev.get("rrule") is not None or ev.get("rdate") is not None,
        "all_day": start is not None and not isinstance(start_dt, datetime.datetime),
    }
    return entry


def freq_of(ev):
    rule = ev.get("rrule")
    if rule is None:
        return None
    return str(rule.get("FREQ", [""])[0])


def expansion(cals):
    events = [e for k in cals for e in k.walk("VEVENT")]
    starts = [e.get("dtstart").dt for e in events if e.get("dtstart") is not None]
    if not starts:
        return None
    firsts = [x.date() if isinstance(x, datetime.datetime) else x for x in starts]
    lower = min(firsts) - datetime.timedelta(days=1)
    small = any(freq_of(e) in ("HOURLY", "MINUTELY", "SECONDLY") for e in events)
    upper = lower + datetime.timedelta(days=4 if small else 730)
    date_uids = set()
    for e in events:
        st = e.get("dtstart")
        if st is not None and str(st.params.get("VALUE", "")).upper() == "DATE":
            date_uids.add(str(e.get("uid")))
    found = []
    for k in cals:
        plain = copy.deepcopy(k)
        if "X-WR-TIMEZONE" in plain:
            del plain["X-WR-TIMEZONE"]  # the library keeps floating times floating
        for occurrence in recurring_ical_events.of(plain).between(lower, upper):
            prop = occurrence.get("dtstart")
            value = prop.dt
            if str(prop.params.get("TZID", "")) in UNRESOLVED_TZIDS - DEFINED_TZIDS and isinstance(value, datetime.datetime):
                value = value.replace(tzinfo=None)
            if str(prop.params.get("VALUE", "")).upper() == "DATE" or str(occurrence.get("uid")) in date_uids:
                if isinstance(value, datetime.datetime):
                    value = value.date()
            found.append((dt_str(value), str(occurrence.get("uid"))))
    found.sort()
    return {
        "from": lower.strftime("%Y-%m-%dT00:00:00Z"),
        "to": upper.strftime("%Y-%m-%dT00:00:00Z"),
        "total_in_window": len(found),
        "first": [{"start": x, "uid": u} for x, u in found[:EXPANSION_COUNT]],
    }


def decode_bytes(data):
    """Independent decoding of the fixtures' unusual encodings (Python codecs, not X-Ray.Ics)."""
    if data.startswith(b"\xef\xbb\xbf"):
        return data[3:]
    if data.startswith(b"\xff\xfe") or data.startswith(b"\xfe\xff"):
        return data.decode("utf-16").encode("utf-8")
    if len(data) > 4 and data[1:2] == b"\x00" and data[3:4] == b"\x00":
        return data.decode("utf-16-le").encode("utf-8")
    if len(data) > 4 and data[0:1] == b"\x00" and data[2:3] == b"\x00":
        return data.decode("utf-16-be").encode("utf-8")
    try:
        data.decode("utf-8")
        return data
    except UnicodeDecodeError:
        return data.decode("cp1252", errors="replace").encode("utf-8")


def preprocess_qp(text):
    """Decodes ENCODING=QUOTED-PRINTABLE values with Python's quopri (the oracle does not), re-escaping them as TEXT."""
    lines = text.split("\r\n")
    out = []
    i = 0
    while i < len(lines):
        line = lines[i]
        i += 1
        head = line.split(":", 1)[0].upper() if ":" in line else ""
        if "QUOTED-PRINTABLE" not in head:
            out.append(line)
            continue
        while line.endswith("=") and i < len(lines):
            line = line[:-1] + lines[i]
            i += 1
        name_and_params, value = line.split(":", 1)
        charset = "cp1252"
        params = []
        for part in name_and_params.split(";"):
            up = part.upper()
            if up.startswith("CHARSET="):
                charset = part.split("=", 1)[1]
                if charset.lower() in ("iso-8859-1", "latin1"):
                    charset = "cp1252"
            elif up.startswith("ENCODING=") or up == "QUOTED-PRINTABLE":
                continue
            else:
                params.append(part)
        raw = quopri.decodestring(value.encode("utf-8", errors="replace"))
        try:
            decoded = raw.decode(charset)
        except (UnicodeDecodeError, LookupError):
            decoded = raw.decode("cp1252", errors="replace")
        unfolded = decoded.replace("\\", "\\\\").replace(";", "\\;").replace(",", "\\,").replace("\r\n", "\\n").replace("\n", "\\n")
        out.append(";".join(params) + ":" + unfolded)
    return "\r\n".join(out)


def parse_all(data):
    """Parses one or several concatenated calendars; line ends are normalised to CRLF first."""
    text = decode_bytes(data).decode("utf-8")
    text = text.replace("\r\n", "\n").replace("\r", "\n").replace("\n", "\r\n")
    text = preprocess_qp(text)
    chunks = [c for c in text.split("BEGIN:VCALENDAR") if c.strip()]
    if len(chunks) <= 1:
        return [Calendar.from_ical(text)]
    return [Calendar.from_ical("BEGIN:VCALENDAR" + c) for c in chunks]


def describe(path):
    data = open(path, "rb").read()
    entry = {"bytes": len(data)}
    try:
        cals = parse_all(data)
    except Exception as error:  # the oracle could not read it; the library is only held to "does not crash"
        entry["parsed"] = False
        entry["error"] = type(error).__name__
        return entry
    cal = cals[0]
    DEFINED_TZIDS.clear()
    DEFINED_TZIDS.update(str(z.get("tzid")) for k in cals for z in k.walk("VTIMEZONE") if z.get("tzid") is not None)
    entry["parsed"] = True
    version = cal.get("version")
    entry["version"] = str(version) if version is not None and not isinstance(version, list) else None
    entry["method"] = str(cal.get("method")).upper() if cal.get("method") is not None else None
    walk = lambda name: [c for k in cals for c in k.walk(name)]
    entry["counts"] = {n: len(walk(n)) for n in ("VEVENT", "VTODO", "VJOURNAL", "VFREEBUSY", "VTIMEZONE")}
    try:
        events = walk("VEVENT")
        entry["events"] = [event_entry(e) for e in events]
        sizes = iter(raw_binary_sizes(data))
        for ev_entry in entry["events"]:
            for att in ev_entry["attachments"]:
                if att["uri"] is None and not att.get("inline_data_uri"):
                    att["size"] = next(sizes, att["size"])
        try:
            entry["expansion"] = expansion(cals)
        except Exception as error:
            entry["expansion"] = None
            entry["expansion_error"] = type(error).__name__
    except Exception as error:
        entry["parsed"] = False
        entry["error"] = "fields:" + type(error).__name__
    return entry


# Expectations the oracle cannot supply or where the library is deliberately different. Each value
# replaces the same key of the generated entry. Keys are paths relative to <test_documents>/ics/.
OVERRIDES = {
    "ical-net/Serialization/Parameter1.ics": {
        "reason": "DTSTART;VALUE=DATE;VALUE=OTHER: the last VALUE wins (a date-time), DTEND;VALUE=DATE with a time is read as the date it declares.",
        "events": [{"uid": "fadc65bd4f5d746ce4a581f072ca66ea", "start": "2009-01-17T20:00:00", "end": "2009-01-17"}],
        "partial": True,
        "expansion": {"from": "2009-01-16T00:00:00Z", "to": "2011-01-16T00:00:00Z", "first": [{"start": "2009-01-17T20:00:00", "uid": "fadc65bd4f5d746ce4a581f072ca66ea"}]},
    },
    "icalendar-py/tests/events/event_with_recurrence.ics": {
        "reason": "Floating DTSTART with UTC EXDATEs: the oracle returns UTC instants, the library keeps the time floating and excludes by wall clock.",
        "expansion": {"from": "1996-03-31T00:00:00Z", "to": "1998-03-31T00:00:00Z", "first": [
            {"start": "1996-04-01T01:00:00", "uid": "None"}] + [{"start": "1996-04-%02dT01:00:00" % d, "uid": "None"} for d in range(5, 16)]},
    },
    "icalendar-py/fuzzing/corpus/event_with_recurrence.ics": {
        "reason": "Floating DTSTART with UTC EXDATEs: the oracle returns UTC instants, the library keeps the time floating and excludes by wall clock.",
        "expansion": {"from": "1996-03-31T00:00:00Z", "to": "1998-03-31T00:00:00Z", "first": [
            {"start": "1996-04-01T01:00:00", "uid": "None"}] + [{"start": "1996-04-%02dT01:00:00" % d, "uid": "None"} for d in range(5, 16)]},
    },
    "icalendar-py/tests/calendars/issue_178_component_with_invalid_name_represented.ics": {
        "reason": "No calendar and no calendar component at all (one empty custom component): not a calendar.",
        "unparsed": True,
    },
    "xray-ics/attachment-damaged-base64.ics": {
        "reason": "Base64 with stray characters or missing padding: the library skips the strays and decodes what is there (the oracle takes it for a URI).",
        "events": [{"uid": "att-bad@example.com", "attachments": [
            {"name": "broken.bin", "content_type": None, "uri": None, "size": 16},
            {"name": "short.bin", "content_type": None, "uri": None, "size": 5}]}],
        "partial": True,
    },
    "icalendar-py/tests/calendars/issue_178_custom_component_contains_other.ics": {
        "reason": "A VEVENT nested inside a custom component: the oracle walks into it, the library wraps the stream in a calendar but only lists a calendar's own events.",
        "counts": {"VEVENT": 0},
        "events": [],
        "expansion": None,
    },
    "xray-ics/vcalendar-1.0-8bit-windows1252.ics": {
        "reason": "vCalendar 1.0 with a TZ of -05:00: its zone-less times are read at that offset (the oracle leaves them floating).",
        "version": "1.0",
        "events": [{"uid": "v1-8bit@example.com", "summary": "Besprechung im B\u00fcro \u20ac5", "location": "Z\u00fcrich",
                    "description": "Caf\u00e9 \u201cquoted\u201d", "start": "2026-03-15T13:00:00Z", "end": "2026-03-15T14:00:00Z"}],
        "partial": True,
        "expansion": {"from": "2026-03-14T00:00:00Z", "to": "2028-03-14T00:00:00Z", "first": [{"start": "2026-03-15T13:00:00Z", "uid": "v1-8bit@example.com"}]},
    },
    "xray-ics/rrule-override-and-cancel.ics": {
        "reason": "A RECURRENCE-ID override with STATUS:CANCELLED removes that instance; the oracle returns it and leaves the filtering to the caller.",
        "expansion": {"from": "2026-01-04T00:00:00Z", "to": "2028-01-04T00:00:00Z", "first": [
            {"start": "2026-01-05T09:00:00Z", "uid": "series@example.com"},
            {"start": "2026-01-06T13:00:00Z", "uid": "series@example.com"},
            {"start": "2026-01-07T09:00:00Z", "uid": "series@example.com"},
            {"start": "2026-01-09T09:00:00Z", "uid": "series@example.com"}]},
    },
    "icalendar-py/tests/calendars/broken_ical.ics": {
        "reason": "DTSTART;;VALUE=DATE-TIME: has an empty parameter. The library skips it and reads the value; the oracle gives up on the date.",
        "events": [{"uid": "abc", "start": "2014-04-09T09:30:00", "end": "2014-04-09T09:30:00"}],
        "partial": True,
    },
    "icalendar-py/tests/calendars/invalid_duration.ics": {
        "reason": "DURATION:P999999999999999999W is unreadable: the library ends the event at its start, the oracle has no end.",
        "events": [{"uid": "invalid-duration@example.com", "end": "2024-01-01T00:00:00Z"}],
        "partial": True,
    },
    "ical-net/Serialization/DateTime2.ics": {
        "reason": "A date DTSTART with a date-time DTEND: the library reads both values as written; the oracle cannot compute an end.",
        "events": [{"uid": None, "summary": "Testing", "start": "1997-04-12", "end": "1997-04-12T02:00:00"}],
        "partial": True,
        "expansion": {"from": "1997-04-11T00:00:00Z", "to": "1999-04-11T00:00:00Z", "first": [{"start": "1997-04-12", "uid": "None"}]},
    },
    "xray-ics/encoding-per-line-charset.ics": {
        "reason": "Per-line CHARSET on 8-bit text: the oracle reads the bytes as UTF-8 only.",
        "events": [{"uid": "enc-3@example.com", "summary": "会議の予定", "location": "Москва"}],
        "partial": True,
    },
    "xray-ics/vcalendar-1.0-quoted-printable.ics": {
        "reason": "vCalendar 1.0: the oracle does not decode quoted-printable. Values derived by hand from the file.",
        "version": "1.0",
        "counts": {"VEVENT": 3, "VTODO": 1, "VJOURNAL": 0, "VFREEBUSY": 0, "VTIMEZONE": 0},
        "events": [
            {"uid": "v1-1@example.com", "summary": "Besprechung äöü € wichtig",
             "description": "Erste Zeile\nZweite Zeile mit ä und eine sehr lange Zeile, die mit einem weichen Zeilenumbruch fortgesetzt wird\nDritte Zeile",
             "location": "Zürich, Büro 5", "start": "2026-03-16T07:00:00Z", "end": "2026-03-16T08:00:00Z"},
            {"uid": "v1-2@example.com", "summary": "Daily legacy", "start": "2026-04-01T12:00:00Z"},
            {"uid": "v1-3@example.com", "summary": "Monthly legacy", "start": "2026-05-01T12:00:00Z"},
        ],
        "partial": True,
        "expansion": {"from": "2026-03-01T00:00:00Z", "to": "2026-08-01T00:00:00Z", "first": [
            {"start": "2026-03-16T07:00:00Z", "uid": "v1-1@example.com"},
            {"start": "2026-03-18T07:00:00Z", "uid": "v1-1@example.com"},
            {"start": "2026-03-23T07:00:00Z", "uid": "v1-1@example.com"},
            {"start": "2026-03-25T07:00:00Z", "uid": "v1-1@example.com"},
            {"start": "2026-04-01T12:00:00Z", "uid": "v1-2@example.com"},
            {"start": "2026-04-03T12:00:00Z", "uid": "v1-2@example.com"},
            {"start": "2026-04-05T12:00:00Z", "uid": "v1-2@example.com"},
            {"start": "2026-04-07T12:00:00Z", "uid": "v1-2@example.com"},
            {"start": "2026-04-09T12:00:00Z", "uid": "v1-2@example.com"},
            {"start": "2026-05-01T12:00:00Z", "uid": "v1-3@example.com"},
            {"start": "2026-05-15T12:00:00Z", "uid": "v1-3@example.com"},
            {"start": "2026-06-01T12:00:00Z", "uid": "v1-3@example.com"},
        ]},
    },
    "xray-ics/tzid-unknown.ics": {
        "reason": "An unresolvable TZID: the library keeps the wall clock as a time with no offset (reported without Z).",
        "events": [{"uid": "tz-unknown@example.com", "summary": "Unresolvable zone", "start": "2026-07-15T09:00:00", "end": "2026-07-15T10:00:00"}],
        "partial": True,
    },
    "xray-ics/malformed-truncated-mid-line.ics": {
        "reason": "Truncation inside DESCRIPTION: everything before the cut survives, the partial line is kept as far as it goes.",
        "counts": {"VEVENT": 1},
        "events": [{"uid": "trunc@example.com", "summary": "Truncated subject line", "description": "This description is c", "start": "2026-01-05T10:00:00Z"}],
        "partial": True,
    },
    "xray-ics/malformed-missing-end.ics": {
        "reason": "Unclosed VEVENT and VCALENDAR are closed at end of input.",
        "counts": {"VEVENT": 1},
        "events": [{"uid": "trunc@example.com", "summary": "Truncated subject line", "start": "2026-01-05T10:00:00Z", "end": "2026-01-05T11:00:00Z"}],
        "partial": True,
    },
    "xray-ics/malformed-stray-end.ics": {
        "reason": "A stray END is ignored.",
        "counts": {"VEVENT": 1},
        "events": [{"uid": "trunc@example.com", "summary": "Truncated subject line"}],
        "partial": True,
    },
    "xray-ics/malformed-garbage-lines.ics": {
        "reason": "Lines that are not content lines are skipped.",
        "counts": {"VEVENT": 1},
        "events": [{"uid": "trunc@example.com", "summary": "Truncated subject line", "start": "2026-01-05T10:00:00Z"}],
        "partial": True,
    },
    "xray-ics/malformed-no-version-prodid.ics": {
        "reason": "VERSION and PRODID are not required by the reader.",
        "counts": {"VEVENT": 1},
        "events": [{"uid": "nv@example.com", "summary": "No version", "start": "2026-01-05T10:00:00Z"}],
        "partial": True,
    },
    "xray-ics/malformed-bad-dates.ics": {
        "reason": "Unreadable dates become null, not an error.",
        "counts": {"VEVENT": 1},
        "events": [{"uid": "bad-dates@example.com", "summary": "Bad dates", "start": None, "end": None, "created": None}],
        "partial": True,
    },
    "xray-ics/malformed-event-without-wrapper.ics": {
        "reason": "A VEVENT outside a VCALENDAR is wrapped in one.",
        "counts": {"VEVENT": 1},
        "events": [{"uid": "nowrap@example.com", "summary": "No VCALENDAR wrapper", "start": "2026-01-05T10:00:00Z"}],
        "partial": True,
    },
    "xray-ics/malformed-unclosed-quote.ics": {
        "reason": "An unterminated quote in a parameter does not swallow the rest of the line.",
        "counts": {"VEVENT": 1},
        "events": [{"uid": "uq@example.com", "summary": "After unclosed quote"}],
        "partial": True,
    },
    "xray-ics/malformed-deep-nesting.ics": {
        "reason": "Components nested past the depth limit are skipped; the event after them is read.",
        "counts": {"VEVENT": 1},
        "events": [{"uid": "after-deep@example.com", "summary": "After deep nesting"}],
        "partial": True,
    },
    "xray-ics/malformed-binary-noise.ics": {
        "reason": "Control and invalid bytes inside a value do not stop the reader.",
        "counts": {"VEVENT": 1},
        "events": [{"uid": "noise@example.com", "start": "2026-01-05T10:00:00Z"}],
        "partial": True,
    },
    "xray-ics/rrule-garbage.ics": {
        "reason": "An invalid RRULE is ignored: the event is a single instance at DTSTART.",
        "expansion": {"from": "2026-01-04T00:00:00Z", "to": "2028-01-04T00:00:00Z", "first": [{"start": "2026-01-05T09:00:00Z", "uid": "garbage@example.com"}]},
        "partial": True,
    },
}


def main(root, out):
    manifest = {}
    for source in SOURCES:
        base = os.path.join(root, source)
        for folder, _, files in os.walk(base):
            for name in sorted(files):
                if not name.lower().endswith((".ics", ".txt", ".vcf", ".bin")) or name in ("README.md", "LICENSE"):
                    continue
                path = os.path.join(folder, name)
                key = os.path.relpath(path, os.path.join(root, "ics")).replace(os.sep, "/")
                if key.endswith(("LICENSE.rst", "license.md")):
                    continue
                if name.startswith("not-a-calendar"):
                    manifest[key] = {"bytes": os.path.getsize(path), "parsed": False, "not_a_calendar": True}
                    continue
                entry = describe(path)
                if key in OVERRIDES:
                    override = dict(OVERRIDES[key])
                    entry["override_reason"] = override.pop("reason")
                    partial = override.pop("partial", False)
                    for field, value in override.items():
                        if partial and field == "events":
                            entry["events"] = value
                            entry["events_partial"] = True
                        else:
                            entry[field] = value
                    entry["parsed"] = not override.get("unparsed", False)
                    entry.pop("unparsed", None)
                manifest[key] = entry
    manifest = {k: manifest[k] for k in sorted(manifest)}
    with open(out, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(manifest, handle, ensure_ascii=False, indent=1, sort_keys=True)
        handle.write("\n")
    parsed = sum(1 for v in manifest.values() if v.get("parsed"))
    print(f"{len(manifest)} fixtures, {parsed} readable by the oracle")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
