#!/usr/bin/env python3
"""Writes the hand-crafted iCalendar fixtures of X-Ray.Ics (test_documents/ics/xray-ics).

Usage: python3 -I make_ics_fixtures.py <test_documents>

Each fixture isolates one tricky case. Bytes are written exactly (line endings, BOMs, code pages), so
the output is deterministic. Expectations are not stored here; build_manifest.py derives them from
independent readers and from the explicit OVERRIDES for what those readers cannot read.
"""
import base64
import os
import sys

CRLF = "\r\n"


def cal(*lines, prodid="-//X-Ray.Ics//fixtures//EN", method=None, extra=()):
    head = ["BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:" + prodid]
    if method:
        head.append("METHOD:" + method)
    head.extend(extra)
    return head + list(lines) + ["END:VCALENDAR"]


def event(*props):
    return ["BEGIN:VEVENT"] + list(props) + ["END:VEVENT"]


def flat(*parts):
    out = []
    for p in parts:
        out.extend(p)
    return out


def text(lines, eol=CRLF):
    return (eol.join(lines) + eol)


BERLIN_TZ = [
    "BEGIN:VTIMEZONE", "TZID:Europe/Berlin",
    "BEGIN:DAYLIGHT", "TZOFFSETFROM:+0100", "TZOFFSETTO:+0200", "TZNAME:CEST",
    "DTSTART:19700329T020000", "RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU", "END:DAYLIGHT",
    "BEGIN:STANDARD", "TZOFFSETFROM:+0200", "TZOFFSETTO:+0100", "TZNAME:CET",
    "DTSTART:19701025T030000", "RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU", "END:STANDARD",
    "END:VTIMEZONE",
]

# A zone whose own rules differ from the IANA ones (so the VTIMEZONE must win): fixed +0530, no DST.
CUSTOM_TZ = [
    "BEGIN:VTIMEZONE", "TZID:Customized Time Zone",
    "BEGIN:STANDARD", "TZOFFSETFROM:+0530", "TZOFFSETTO:+0530", "TZNAME:Custom",
    "DTSTART:16010101T000000", "END:STANDARD",
    "END:VTIMEZONE",
]

# Outlook writes Windows zone names and explicit year-dated rules.
OUTLOOK_TZ = [
    "BEGIN:VTIMEZONE", "TZID:W. Europe Standard Time",
    "BEGIN:STANDARD", "DTSTART:16010101T030000", "TZOFFSETFROM:+0200", "TZOFFSETTO:+0100",
    "RRULE:FREQ=YEARLY;INTERVAL=1;BYDAY=-1SU;BYMONTH=10", "END:STANDARD",
    "BEGIN:DAYLIGHT", "DTSTART:16010101T020000", "TZOFFSETFROM:+0100", "TZOFFSETTO:+0200",
    "RRULE:FREQ=YEARLY;INTERVAL=1;BYDAY=-1SU;BYMONTH=3", "END:DAYLIGHT",
    "END:VTIMEZONE",
]

PNG_1X1 = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")


def b64_lines(data, width=64):
    s = base64.b64encode(data).decode()
    return [s[i:i + width] for i in range(0, len(s), width)] or [""]


def fold(line, width=75):
    """RFC 5545 folding by characters (ASCII-safe)."""
    out, first = [], True
    while len(line) > (width if first else width - 1):
        n = width if first else width - 1
        out.append(line[:n] if first else " " + line[:n])
        line = line[n:]
        first = False
    out.append(line if first else " " + line)
    return out


F = {}


def put(name, data, binary=False):
    F[name] = data if isinstance(data, bytes) else data.encode("utf-8")


# --- basics --------------------------------------------------------------------------------------
put("basic-event.ics", text(cal(*event(
    "UID:basic-1@example.com", "DTSTAMP:20260101T080000Z", "CREATED:20251230T101500Z", "LAST-MODIFIED:20251231T090000Z",
    "DTSTART:20260105T100000Z", "DTEND:20260105T110000Z", "SUMMARY:Project kickoff", "DESCRIPTION:Agenda\\n- scope\\n- plan",
    "LOCATION:Room 4\\, Building A", "STATUS:CONFIRMED", "CLASS:PUBLIC", "TRANSP:OPAQUE", "PRIORITY:3", "SEQUENCE:2",
    "ORGANIZER;CN=Olivia Organizer:mailto:olivia@example.com", "ATTENDEE;CN=Adam Attendee;ROLE=REQ-PARTICIPANT;PARTSTAT=ACCEPTED;RSVP=TRUE:mailto:adam@example.com",
    "CATEGORIES:Work,Planning", "URL:https://example.com/kickoff", "GEO:52.520008;13.404954"))))

put("folded-lines.ics", "".join(l + CRLF for l in cal(*event(
    "UID:folded-1@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260106T090000Z", "DTEND:20260106T100000Z",
    *fold("SUMMARY:" + "A very long subject line that has to be folded because it is far longer than seventy-five octets in total"),
    *fold("DESCRIPTION:" + "Line one of a description that is also long enough to need folding across several physical lines\\nand a second paragraph that continues for a good while more so the fold lands in odd places"),
    "ORGANIZER;CN=Fold Tester:mailto:fold@example.com"))))

# LF-only line ends with tab folding and a fold that splits a word.
lf = "\n".join(cal(*event("UID:folded-lf@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260106T090000Z",
                         "SUMMARY:Tab folded su", "\tbject continues here", "DESCRIPTION:one", " two", "\tthree"))) + "\n"
put("folded-lf-tab.ics", lf)

put("utf8-unicode.ics", text(cal(*event(
    "UID:utf8-1@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260107T090000Z", "DTEND:20260107T100000Z",
    "SUMMARY:Réunion équipe – Café ☕ 日本語 😀", "DESCRIPTION:Zażółć gęślą jaźń\\nΕλληνικά\\nРусский текст",
    "LOCATION:München, Straße 5", "ORGANIZER;CN=\"Zoë Müller\":mailto:zoe@example.com"))))

# --- encodings -----------------------------------------------------------------------------------
u8 = text(cal(*event("UID:enc-1@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260108T090000Z", "DTEND:20260108T100000Z",
                    "SUMMARY:Encoding test äöü € – ✓", "DESCRIPTION:Body äöü\\nSecond line", "LOCATION:Zürich",
                    "ORGANIZER;CN=Jörg Ünal:mailto:joerg@example.com")))
put("encoding-utf8-bom.ics", b"\xef\xbb\xbf" + u8.encode("utf-8"))
put("encoding-utf16le-bom.ics", b"\xff\xfe" + u8.encode("utf-16-le"))
put("encoding-utf16be-bom.ics", b"\xfe\xff" + u8.encode("utf-16-be"))
put("encoding-utf16le-nobom.ics", u8.encode("utf-16-le"))
put("encoding-utf16be-nobom.ics", u8.encode("utf-16-be"))
cp = text(cal(*event("UID:enc-2@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260108T090000Z", "DTEND:20260108T100000Z",
                    "SUMMARY:Legacy äöü € Zürich", "DESCRIPTION:Body äöü\\nSecond line", "LOCATION:Zürich",
                    "ORGANIZER;CN=Jörg Ünal:mailto:joerg@example.com")))
put("encoding-windows1252.ics", cp.encode("cp1252"))
cpj = text(cal(*event("UID:enc-3@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260108T090000Z", "DTEND:20260108T100000Z",
                     "SUMMARY;CHARSET=shift_jis:会議の予定", "LOCATION;CHARSET=windows-1251:Москва")))
# per-line CHARSET on 8-bit text: build the bytes line by line
lines = cal(*event("UID:enc-3@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260108T090000Z", "DTEND:20260108T100000Z"))
head = (CRLF.join(lines[:-2]) + CRLF).encode("ascii")
head += "SUMMARY;CHARSET=shift_jis:会議の予定".encode("shift_jis") + CRLF.encode()
head += "LOCATION;CHARSET=windows-1251:Москва".encode("cp1251") + CRLF.encode()
# the ascii part above contains the charset names; encode the property names/params separately
head = head.replace("会議の予定".encode("shift_jis"), "会議の予定".encode("shift_jis"))
put("encoding-per-line-charset.ics", head + b"END:VEVENT\r\nEND:VCALENDAR\r\n")

# --- line endings --------------------------------------------------------------------------------
mix = ["BEGIN:VCALENDAR\r\n", "VERSION:2.0\n", "PRODID:-//X-Ray.Ics//mixed//EN\r", "BEGIN:VEVENT\r\n", "UID:mixed-1@example.com\n",
       "DTSTAMP:20260101T080000Z\r\n", "DTSTART:20260109T090000Z\n", "SUMMARY:Mixed line ends\r", "DESCRIPTION:folded\r\n across\n lines\r\n",
       "END:VEVENT\n", "END:VCALENDAR\r\n"]
put("mixed-line-endings.ics", "".join(mix))

# --- time zones ----------------------------------------------------------------------------------
put("tzid-vtimezone-berlin.ics", text(cal(*flat(BERLIN_TZ, event(
    "UID:tz-berlin@example.com", "DTSTAMP:20260101T080000Z",
    "DTSTART;TZID=Europe/Berlin:20260706T100000", "DTEND;TZID=Europe/Berlin:20260706T113000", "SUMMARY:Summer meeting in Berlin"),
    event("UID:tz-berlin-winter@example.com", "DTSTAMP:20260101T080000Z",
          "DTSTART;TZID=Europe/Berlin:20260112T100000", "DTEND;TZID=Europe/Berlin:20260112T113000", "SUMMARY:Winter meeting in Berlin")))))

put("tzid-custom-vtimezone.ics", text(cal(*flat(CUSTOM_TZ, event(
    "UID:tz-custom@example.com", "DTSTAMP:20260101T080000Z",
    'DTSTART;TZID="Customized Time Zone":20260706T100000', 'DTEND;TZID="Customized Time Zone":20260706T110000', "SUMMARY:Custom zone")))))

put("tzid-iana-only.ics", text(cal(*event(
    "UID:tz-ny@example.com", "DTSTAMP:20260101T080000Z",
    "DTSTART;TZID=America/New_York:20260115T090000", "DTEND;TZID=America/New_York:20260115T100000", "SUMMARY:New York winter"),
    *event("UID:tz-ny-summer@example.com", "DTSTAMP:20260101T080000Z",
           "DTSTART;TZID=America/New_York:20260715T090000", "DTEND;TZID=America/New_York:20260715T100000", "SUMMARY:New York summer"))))

put("tzid-windows-name.ics", text(cal(*event(
    "UID:tz-win@example.com", "DTSTAMP:20260101T080000Z",
    "DTSTART;TZID=Eastern Standard Time:20260715T090000", "DTEND;TZID=Eastern Standard Time:20260715T100000", "SUMMARY:Windows zone name without VTIMEZONE"))))

put("tzid-mozilla-prefix.ics", text(cal(*event(
    "UID:tz-moz@example.com", "DTSTAMP:20260101T080000Z",
    "DTSTART;TZID=/mozilla.org/20050126_1/Europe/Paris:20260715T090000", "DTEND;TZID=/mozilla.org/20050126_1/Europe/Paris:20260715T100000", "SUMMARY:Mozilla style TZID"))))

put("tzid-unknown.ics", text(cal(*event(
    "UID:tz-unknown@example.com", "DTSTAMP:20260101T080000Z",
    "DTSTART;TZID=Nowhere/Land:20260715T090000", "DTEND;TZID=Nowhere/Land:20260715T100000", "SUMMARY:Unresolvable zone"))))

put("floating-time.ics", text(cal(*event(
    "UID:floating-1@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260310T143000", "DTEND:20260310T153000", "SUMMARY:Floating"))))

put("all-day.ics", text(cal(*event(
    "UID:allday-1@example.com", "DTSTAMP:20260101T080000Z", "DTSTART;VALUE=DATE:20260320", "DTEND;VALUE=DATE:20260321", "SUMMARY:Single all-day"),
    *event("UID:allday-2@example.com", "DTSTAMP:20260101T080000Z", "DTSTART;VALUE=DATE:20260323", "DTEND;VALUE=DATE:20260326", "SUMMARY:Three days"),
    *event("UID:allday-3@example.com", "DTSTAMP:20260101T080000Z", "DTSTART;VALUE=DATE:20260401", "SUMMARY:No end"),
    *event("UID:allday-4@example.com", "DTSTAMP:20260101T080000Z", "DTSTART;VALUE=DATE:20260402", "DURATION:P2D", "SUMMARY:Duration"))))

put("duration.ics", text(cal(*event(
    "UID:dur-1@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260410T090000Z", "DURATION:PT1H30M", "SUMMARY:Duration only"),
    *event("UID:dur-2@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260411T090000Z", "DURATION:P1DT2H", "SUMMARY:Day and hours"))))

# --- recurrence ----------------------------------------------------------------------------------
def rr(name, uid, rrule, dtstart="DTSTART:20260105T090000Z", dtend="DTEND:20260105T100000Z", more=(), tz=()):
    put(name, text(cal(*flat(tz, event("UID:" + uid + "@example.com", "DTSTAMP:20260101T080000Z", dtstart, dtend,
                                         "SUMMARY:" + uid, "RRULE:" + rrule, *more)))))


rr("rrule-daily-count.ics", "daily-count", "FREQ=DAILY;COUNT=5")
rr("rrule-daily-interval-until.ics", "daily-until", "FREQ=DAILY;INTERVAL=3;UNTIL=20260131T090000Z")
rr("rrule-weekly-byday.ics", "weekly-byday", "FREQ=WEEKLY;BYDAY=MO,WE,FR;COUNT=12")
rr("rrule-weekly-interval-wkst.ics", "weekly-wkst", "FREQ=WEEKLY;INTERVAL=2;BYDAY=SU,MO;WKST=SU;COUNT=10",
   dtstart="DTSTART:20260104T090000Z", dtend="DTEND:20260104T100000Z")
rr("rrule-weekly-wkst-mo.ics", "weekly-wkst-mo", "FREQ=WEEKLY;INTERVAL=2;BYDAY=SU,MO;WKST=MO;COUNT=10",
   dtstart="DTSTART:20260104T090000Z", dtend="DTEND:20260104T100000Z")
rr("rrule-monthly-bymonthday.ics", "monthly-bymd", "FREQ=MONTHLY;BYMONTHDAY=1,15,-1;COUNT=12")
rr("rrule-monthly-31st.ics", "monthly-31", "FREQ=MONTHLY;COUNT=8", dtstart="DTSTART:20260131T090000Z", dtend="DTEND:20260131T100000Z")
rr("rrule-monthly-nth-weekday.ics", "monthly-nth", "FREQ=MONTHLY;BYDAY=2TU;COUNT=8", dtstart="DTSTART:20260113T090000Z", dtend="DTEND:20260113T100000Z")
rr("rrule-monthly-last-friday.ics", "monthly-last-fri", "FREQ=MONTHLY;BYDAY=-1FR;COUNT=8", dtstart="DTSTART:20260130T090000Z", dtend="DTEND:20260130T100000Z")
rr("rrule-monthly-bysetpos.ics", "monthly-setpos", "FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1;COUNT=8", dtstart="DTSTART:20260130T090000Z", dtend="DTEND:20260130T100000Z")
rr("rrule-monthly-bysetpos-first.ics", "monthly-setpos-first", "FREQ=MONTHLY;BYDAY=SA,SU;BYSETPOS=1;COUNT=8", dtstart="DTSTART:20260103T090000Z", dtend="DTEND:20260103T100000Z")
rr("rrule-yearly-simple.ics", "yearly", "FREQ=YEARLY;COUNT=6", dtstart="DTSTART:20240229T090000Z", dtend="DTEND:20240229T100000Z")
rr("rrule-yearly-bymonth.ics", "yearly-bymonth", "FREQ=YEARLY;BYMONTH=3,9;BYDAY=1MO;COUNT=8", dtstart="DTSTART:20260302T090000Z", dtend="DTEND:20260302T100000Z")
rr("rrule-yearly-byyearday.ics", "yearly-byyearday", "FREQ=YEARLY;BYYEARDAY=1,100,-1;COUNT=9", dtstart="DTSTART:20260101T090000Z", dtend="DTEND:20260101T100000Z")
rr("rrule-yearly-byweekno.ics", "yearly-byweekno", "FREQ=YEARLY;BYWEEKNO=20;BYDAY=MO;COUNT=4", dtstart="DTSTART:20260511T090000Z", dtend="DTEND:20260511T100000Z")
rr("rrule-yearly-bymonthday-bymonth.ics", "yearly-md", "FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=29;COUNT=3", dtstart="DTSTART:20240229T090000Z", dtend="DTEND:20240229T100000Z")
rr("rrule-hourly.ics", "hourly", "FREQ=HOURLY;INTERVAL=5;COUNT=20", dtstart="DTSTART:20260105T000000Z", dtend="DTEND:20260105T003000Z")
rr("rrule-minutely-byhour.ics", "minutely-byhour", "FREQ=MINUTELY;INTERVAL=30;BYHOUR=9,10;COUNT=20", dtstart="DTSTART:20260105T090000Z", dtend="DTEND:20260105T092000Z")
rr("rrule-daily-byhour-byminute.ics", "daily-byhm", "FREQ=DAILY;BYHOUR=9,17;BYMINUTE=0,30;COUNT=12", dtstart="DTSTART:20260105T090000Z", dtend="DTEND:20260105T093000Z")
rr("rrule-daily-byday-limit.ics", "daily-byday", "FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR;COUNT=12")
rr("rrule-exdate-rdate.ics", "ex-rdate", "FREQ=DAILY;COUNT=8",
   more=["EXDATE:20260107T090000Z,20260109T090000Z", "RDATE:20260120T090000Z", "RDATE;VALUE=DATE-TIME:20260122T090000Z"])
rr("rrule-exdate-tzid.ics", "ex-tzid", "FREQ=WEEKLY;COUNT=6", dtstart="DTSTART;TZID=Europe/Berlin:20260105T090000", dtend="DTEND;TZID=Europe/Berlin:20260105T100000",
   more=["EXDATE;TZID=Europe/Berlin:20260119T090000"], tz=BERLIN_TZ)
rr("rrule-dst-crossing.ics", "dst", "FREQ=WEEKLY;COUNT=12", dtstart="DTSTART;TZID=Europe/Berlin:20260316T090000", dtend="DTEND;TZID=Europe/Berlin:20260316T100000", tz=BERLIN_TZ)
rr("rrule-dst-crossing-iana.ics", "dst-iana", "FREQ=WEEKLY;COUNT=12", dtstart="DTSTART;TZID=America/New_York:20260302T090000", dtend="DTEND;TZID=America/New_York:20260302T100000")
rr("rrule-allday.ics", "allday-weekly", "FREQ=WEEKLY;COUNT=5", dtstart="DTSTART;VALUE=DATE:20260105", dtend="DTEND;VALUE=DATE:20260106")
rr("rrule-allday-exdate.ics", "allday-exdate", "FREQ=DAILY;COUNT=6", dtstart="DTSTART;VALUE=DATE:20260105", dtend="DTEND;VALUE=DATE:20260106",
   more=["EXDATE;VALUE=DATE:20260107"])
rr("rrule-until-date.ics", "until-date", "FREQ=DAILY;UNTIL=20260110", dtstart="DTSTART;VALUE=DATE:20260105", dtend="DTEND;VALUE=DATE:20260106")
rr("rrule-floating.ics", "floating-daily", "FREQ=DAILY;COUNT=4", dtstart="DTSTART:20260105T093000", dtend="DTEND:20260105T103000")
rr("rrule-forever.ics", "forever", "FREQ=WEEKLY;BYDAY=TU", dtstart="DTSTART:20260106T090000Z", dtend="DTEND:20260106T100000Z")
rr("rrule-lowercase.ics", "lowercase", "freq=weekly;byday=mo;count=3")
rr("rrule-garbage.ics", "garbage", "FREQ=SOMETIMES;COUNT=x")

put("rrule-override-and-cancel.ics", text(cal(
    *event("UID:series@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260105T090000Z", "DTEND:20260105T100000Z",
           "SUMMARY:Standup", "RRULE:FREQ=DAILY;COUNT=5"),
    *event("UID:series@example.com", "DTSTAMP:20260101T080000Z", "RECURRENCE-ID:20260106T090000Z", "DTSTART:20260106T130000Z",
           "DTEND:20260106T140000Z", "SUMMARY:Standup (moved)"),
    *event("UID:series@example.com", "DTSTAMP:20260101T080000Z", "RECURRENCE-ID:20260108T090000Z", "DTSTART:20260108T090000Z",
           "DTEND:20260108T100000Z", "SUMMARY:Standup", "STATUS:CANCELLED"))))

# --- attendees -----------------------------------------------------------------------------------
put("attendees-variety.ics", text(cal(*event(
    "UID:att-1@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260201T090000Z", "DTEND:20260201T100000Z", "SUMMARY:Attendee variety",
    "ORGANIZER;CN=Olivia Organizer;SENT-BY=\"mailto:assistant@example.com\":mailto:Olivia@Example.COM",
    "ATTENDEE;CN=Required One;ROLE=REQ-PARTICIPANT;PARTSTAT=ACCEPTED;RSVP=TRUE:mailto:req1@example.com",
    "ATTENDEE:mailto:nocn@example.com",
    "ATTENDEE;CN=\"Comma, Name; Semi\";PARTSTAT=TENTATIVE:mailto:comma@example.com",
    "ATTENDEE;CN=Optional Person;ROLE=OPT-PARTICIPANT;PARTSTAT=DECLINED;RSVP=FALSE:MAILTO:opt1@example.com",
    "ATTENDEE;CN=Optional Two;ROLE=OPT-PARTICIPANT;PARTSTAT=NEEDS-ACTION:mailto:opt2@example.com",
    "ATTENDEE;CN=Chair Person;ROLE=CHAIR;PARTSTAT=ACCEPTED:mailto:chair@example.com",
    "ATTENDEE;CN=Observer;ROLE=NON-PARTICIPANT:mailto:observer@example.com",
    "ATTENDEE;CN=Board Room;CUTYPE=ROOM;ROLE=NON-PARTICIPANT;PARTSTAT=ACCEPTED:mailto:room1@example.com",
    "ATTENDEE;CN=Projector;CUTYPE=RESOURCE:mailto:projector@example.com",
    "ATTENDEE;CN=Dev Team;CUTYPE=GROUP;MEMBER=\"mailto:dev@example.com\":mailto:team@example.com",
    "ATTENDEE;CN=Delegate;PARTSTAT=DELEGATED;DELEGATED-TO=\"mailto:deputy@example.com\":mailto:boss@example.com",
    "ATTENDEE;CN=Deputy;DELEGATED-FROM=\"mailto:boss@example.com\";PARTSTAT=ACCEPTED:mailto:deputy@example.com",
    "ATTENDEE;CN=Apple Style;EMAIL=apple@example.com:/principals/users/apple/",
    "ATTENDEE;CN=Urn Style;EMAIL=urn@example.com:urn:uuid:11111111-2222-3333-4444-555555555555",
    "ATTENDEE;CN=Exchange DN:/O=EXAMPLE/OU=FIRST/cn=Recipients/cn=ex1",
    "ATTENDEE;CN=Bare Address:bare@example.com",
    "ATTENDEE;CN=Ünïcode Näme;PARTSTAT=ACCEPTED:mailto:unicode@example.com"))))

# --- attachments ---------------------------------------------------------------------------------
png = b64_lines(PNG_1X1, 40)
txt = b64_lines(b"Hello attachment.\nSecond line.\n", 40)
att = cal(*event("UID:att-files@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260202T090000Z", "DTEND:20260202T100000Z",
                 "SUMMARY:Attachments", "DESCRIPTION:See attached files"))
att = att[:-2]
att.append("ATTACH;ENCODING=BASE64;VALUE=BINARY;FMTTYPE=image/png;FILENAME=pixel.png:" + png[0])
att.extend(" " + l for l in png[1:])
att.append("ATTACH;ENCODING=BASE64;VALUE=BINARY;FMTTYPE=text/plain;X-FILENAME=notes.txt:" + txt[0])
att.extend(" " + l for l in txt[1:])
att.append("ATTACH;ENCODING=BASE64;VALUE=BINARY:" + txt[0])
att.extend(" " + l for l in txt[1:])
att.append("ATTACH;FMTTYPE=application/pdf;FILENAME=report.pdf:https://example.com/files/report.pdf")
att.append("ATTACH:https://example.com/files/some%20document.docx")
att.append("ATTACH:ftp://example.com/")
att.append("ATTACH;FMTTYPE=text/plain;FILENAME=inline-uri.txt:data:text/plain;base64,SGVsbG8gZGF0YSBVUkk=")
att.extend(["END:VEVENT", "END:VCALENDAR"])
put("attachments-inline-and-uri.ics", text(att))

put("attachment-damaged-base64.ics", text(cal(*event(
    "UID:att-bad@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260202T090000Z", "SUMMARY:Bad base64",
    "ATTACH;ENCODING=BASE64;VALUE=BINARY;FILENAME=broken.bin:SGVsbG8gd29ybGQ!!!@@@ SGVsbG8", "ATTACH;ENCODING=BASE64;VALUE=BINARY;FILENAME=short.bin:SGVsbG8"))))

# --- text escaping, parameters, X- properties -----------------------------------------------------
put("text-escapes.ics", text(cal(*event(
    "UID:esc-1@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260203T090000Z", "SUMMARY:Semi\\; comma\\, backslash\\\\ done",
    "DESCRIPTION:Line1\\nLine2\\NLine3\\, with comma\\; and semi \\\\ and path C:\\Users\\x and colon\\: end",
    "LOCATION:Hall 1\\, 2nd floor", "COMMENT:Quote \\\" mark"))))

put("param-quoting.ics", text(cal(*event(
    "UID:param-1@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260204T090000Z", "SUMMARY:Params",
    'ATTENDEE;CN="Last, First";X-NOTE="a;b:c,d";PARTSTAT=ACCEPTED:mailto:q1@example.com',
    'ATTENDEE;CN="Caret ^'"'"'quoted^'"'"' and ^^ and^nnewline":mailto:q2@example.com',
    'X-MULTI;TYPE=WORK,HOME,"A,B":value',
    'ORGANIZER;CN="Colon: Name":mailto:q3@example.com'))))

put("x-properties.ics", text(cal(*event(
    "UID:xprop-1@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260205T090000Z", "SUMMARY:X props",
    "X-CUSTOM-ONE:custom value", "X-CUSTOM-TWO;PARAM=1:another", "X-MICROSOFT-CDO-BUSYSTATUS:BUSY", "item1.X-GROUPED;TYPE=a:grouped value"),
    extra=["X-WR-CALNAME:X Calendar", "X-WR-CALDESC:About", "X-WR-TIMEZONE:Europe/Berlin", "X-PUBLISHED-TTL:PT1H"])))

put("multi-event.ics", text(cal(
    *event("UID:m1@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260301T090000Z", "SUMMARY:First"),
    *event("UID:m2@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260302T090000Z", "SUMMARY:Second"),
    *event("UID:m3@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260303T090000Z", "SUMMARY:Third"),
    "BEGIN:VTODO", "UID:t1@example.com", "DTSTAMP:20260101T080000Z", "SUMMARY:A todo", "DUE:20260310T170000Z", "PERCENT-COMPLETE:40", "PRIORITY:1", "STATUS:IN-PROCESS", "END:VTODO",
    "BEGIN:VJOURNAL", "UID:j1@example.com", "DTSTAMP:20260101T080000Z", "DTSTART;VALUE=DATE:20260304", "SUMMARY:A journal", "DESCRIPTION:Dear diary", "END:VJOURNAL",
    "BEGIN:VFREEBUSY", "UID:f1@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260301T000000Z", "DTEND:20260302T000000Z",
    "FREEBUSY;FBTYPE=BUSY:20260301T090000Z/20260301T100000Z,20260301T130000Z/PT1H", "FREEBUSY;FBTYPE=BUSY-TENTATIVE:20260301T150000Z/20260301T160000Z", "END:VFREEBUSY")))

put("two-calendars-concatenated.ics", text(cal(*event("UID:c1@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260301T090000Z", "SUMMARY:From calendar one")))
    + text(cal(*event("UID:c2@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260302T090000Z", "SUMMARY:From calendar two"), method="PUBLISH")))

put("alarms.ics", text(cal(*event(
    "UID:alarm-1@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260306T090000Z", "DTEND:20260306T100000Z", "SUMMARY:Alarms",
    "BEGIN:VALARM", "ACTION:DISPLAY", "DESCRIPTION:Reminder", "TRIGGER:-PT15M", "END:VALARM",
    "BEGIN:VALARM", "ACTION:AUDIO", "TRIGGER;RELATED=END:PT0S", "REPEAT:2", "DURATION:PT5M", "END:VALARM",
    "BEGIN:VALARM", "ACTION:DISPLAY", "DESCRIPTION:Absolute", "TRIGGER;VALUE=DATE-TIME:20260306T080000Z", "END:VALARM"))))

# --- iTIP -----------------------------------------------------------------------------------------
put("method-request.ics", text(cal(*event(
    "UID:itip-1@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260310T090000Z", "DTEND:20260310T100000Z", "SUMMARY:Please attend", "SEQUENCE:0",
    "ORGANIZER;CN=Boss:mailto:boss@example.com", "ATTENDEE;CN=You;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:you@example.com"), method="REQUEST")))
put("method-cancel.ics", text(cal(*event(
    "UID:itip-1@example.com", "DTSTAMP:20260102T080000Z", "DTSTART:20260310T090000Z", "DTEND:20260310T100000Z", "SUMMARY:Cancelled: Please attend", "SEQUENCE:1", "STATUS:CANCELLED",
    "ORGANIZER;CN=Boss:mailto:boss@example.com", "ATTENDEE;CN=You:mailto:you@example.com"), method="CANCEL")))
put("method-reply.ics", text(cal(*event(
    "UID:itip-1@example.com", "DTSTAMP:20260101T120000Z", "DTSTART:20260310T090000Z", "DTEND:20260310T100000Z", "SUMMARY:Please attend", "SEQUENCE:0",
    "ORGANIZER;CN=Boss:mailto:boss@example.com", "ATTENDEE;CN=You;PARTSTAT=ACCEPTED:mailto:you@example.com"), method="REPLY")))
put("method-counter.ics", text(cal(*event(
    "UID:itip-1@example.com", "DTSTAMP:20260101T120000Z", "DTSTART:20260311T090000Z", "DTEND:20260311T100000Z", "SUMMARY:Please attend",
    "ORGANIZER;CN=Boss:mailto:boss@example.com", "ATTENDEE;CN=You;PARTSTAT=TENTATIVE:mailto:you@example.com", "COMMENT:Can we move it?"), method="COUNTER")))

# --- real-world exports ---------------------------------------------------------------------------
outlook = cal(*flat(OUTLOOK_TZ, [
    "BEGIN:VEVENT",
    "ATTENDEE;CN=Ana Required;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:ana@contoso.example",
    "ATTENDEE;CN=Bob Optional;ROLE=OPT-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:bob@contoso.example",
    "ATTENDEE;CN=Conference Room 1;CUTYPE=RESOURCE;ROLE=NON-PARTICIPANT;RSVP=FALSE:mailto:room1@contoso.example",
    "CLASS:PUBLIC", "CREATED:20260105T150000Z", "DESCRIPTION:Please join the weekly sync.\\n\\nAgenda:\\n1. Status\\n2. Risks\\n\\nThanks\\, Olivia\\n",
    "DTEND;TZID=W. Europe Standard Time:20260112T110000", "DTSTAMP:20260105T150100Z", "DTSTART;TZID=W. Europe Standard Time:20260112T100000",
    "LAST-MODIFIED:20260105T150000Z", "LOCATION:Conference Room 1", "ORGANIZER;CN=Olivia Organizer:mailto:olivia@contoso.example",
    "PRIORITY:5", "SEQUENCE:0", "SUMMARY;LANGUAGE=en-us:Weekly sync", "TRANSP:OPAQUE", "UID:040000008200E00074C5B7101A82E00800000000A0B1C2D3E4F5A6B7",
    "X-ALT-DESC;FMTTYPE=text/html:<html><body><p>Please join the weekly sync.</p><ul><li>Status</li><li>Risks</li></ul></body></html>",
    "X-MICROSOFT-CDO-BUSYSTATUS:TENTATIVE", "X-MICROSOFT-CDO-IMPORTANCE:1", "X-MICROSOFT-CDO-INTENDEDSTATUS:BUSY", "X-MICROSOFT-DISALLOW-COUNTER:FALSE",
    "X-MS-OLK-APPTSEQTIME:20260105T150000Z", "X-MS-OLK-AUTOFILLLOCATION:FALSE", "X-MS-OLK-CONFTYPE:0",
    "BEGIN:VALARM", "TRIGGER:-PT15M", "ACTION:DISPLAY", "DESCRIPTION:Reminder", "END:VALARM", "END:VEVENT"]), method="REQUEST",
    prodid="-//Microsoft Corporation//Outlook 16.0 MIMEDIR//EN", extra=["X-MS-OLK-FORCEINSPECTOROPEN:TRUE", "CALSCALE:GREGORIAN"])
put("outlook-meeting-request.ics", text(outlook))

google = cal(*flat([
    "BEGIN:VTIMEZONE", "TZID:Europe/Zurich", "X-LIC-LOCATION:Europe/Zurich",
    "BEGIN:DAYLIGHT", "TZOFFSETFROM:+0100", "TZOFFSETTO:+0200", "TZNAME:CEST", "DTSTART:19700329T020000", "RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU", "END:DAYLIGHT",
    "BEGIN:STANDARD", "TZOFFSETFROM:+0200", "TZOFFSETTO:+0100", "TZNAME:CET", "DTSTART:19701025T030000", "RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU", "END:STANDARD",
    "END:VTIMEZONE",
    "BEGIN:VEVENT", "DTSTART;TZID=Europe/Zurich:20260311T140000", "DTEND;TZID=Europe/Zurich:20260311T150000", "RRULE:FREQ=WEEKLY;BYDAY=WE",
    "DTSTAMP:20260301T101010Z", "ORGANIZER;CN=Gina Google:mailto:gina@example.org", "UID:abcdef0123456789@google.com",
    "ATTENDEE;CUTYPE=INDIVIDUAL;ROLE=REQ-PARTICIPANT;PARTSTAT=ACCEPTED;RSVP=TRUE;CN=Gina Google;X-NUM-GUESTS=0:mailto:gina@example.org",
    "ATTENDEE;CUTYPE=INDIVIDUAL;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=TRUE;CN=Hans Guest;X-NUM-GUESTS=0:mailto:hans@example.org",
    "X-MICROSOFT-CDO-OWNERAPPTID:1234567890", "CREATED:20260301T100000Z",
    "DESCRIPTION:Weekly review\\nJoin: https://meet.example.org/abc-defg-hij\\n\\n-::~:~::~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~:~::~:~::-\\nInvitation from Google Calendar",
    "LAST-MODIFIED:20260301T100500Z", "LOCATION:Zurich HQ\\, Floor 3", "SEQUENCE:1", "STATUS:CONFIRMED", "SUMMARY:Weekly review", "TRANSP:OPAQUE",
    "ATTACH;FILENAME=agenda.pdf;FMTTYPE=application/pdf:https://drive.example.org/file/d/1AbC/view?usp=drive_web",
    "BEGIN:VALARM", "ACTION:EMAIL", "DESCRIPTION:This is an event reminder", "SUMMARY:Alarm notification", "ATTENDEE:mailto:gina@example.org", "TRIGGER:-P0DT0H30M0S", "END:VALARM",
    "BEGIN:VALARM", "ACTION:DISPLAY", "DESCRIPTION:This is an event reminder", "TRIGGER:-P0DT0H10M0S", "END:VALARM", "END:VEVENT"]),
    prodid="-//Google Inc//Google Calendar 70.9054//EN", method="PUBLISH",
    extra=["CALSCALE:GREGORIAN", "X-WR-CALNAME:Gina Google", "X-WR-TIMEZONE:Europe/Zurich"])
put("google-calendar-export.ics", text(google))

apple = cal(*event(
    "UID:APPLE-1234", "DTSTAMP:20260301T101010Z", "DTSTART;TZID=America/Los_Angeles:20260312T080000", "DTEND;TZID=America/Los_Angeles:20260312T090000", "SUMMARY:Apple event",
    "ORGANIZER;CN=Al Apple;EMAIL=al@icloud.example:/1234567/principal/", "ATTENDEE;CN=Al Apple;CUTYPE=INDIVIDUAL;EMAIL=al@icloud.example;PARTSTAT=ACCEPTED;ROLE=CHAIR:/1234567/principal/",
    "ATTENDEE;CN=Bea Apple;CUTYPE=INDIVIDUAL;EMAIL=bea@icloud.example;PARTSTAT=NEEDS-ACTION;ROLE=REQ-PARTICIPANT;RSVP=TRUE:mailto:bea@icloud.example",
    "X-APPLE-STRUCTURED-LOCATION;VALUE=URI;X-ADDRESS=1 Infinite Loop;X-TITLE=Apple HQ:geo:37.331,-122.030", "LOCATION:Apple HQ\\n1 Infinite Loop",
    "BEGIN:VALARM", "X-WR-ALARMUID:ABCD", "UID:ABCD", "TRIGGER:-PT1H", "ACTION:DISPLAY", "DESCRIPTION:Reminder", "END:VALARM"),
    prodid="-//Apple Inc.//macOS 15.0//EN", extra=["CALSCALE:GREGORIAN"])
put("apple-calendar-export.ics", text(apple))

# --- vCalendar 1.0 ---------------------------------------------------------------------------------
v1 = [
    "BEGIN:VCALENDAR", "VERSION:1.0", "PRODID:-//Legacy PDA//EN", "TZ:+01:00",
    "BEGIN:VEVENT", "DTSTART:20260316T080000", "DTEND:20260316T090000", "UID:v1-1@example.com",
    "SUMMARY;CHARSET=ISO-8859-1;ENCODING=QUOTED-PRINTABLE:Besprechung =E4=F6=FC =80 wichtig",
    "DESCRIPTION;CHARSET=ISO-8859-1;ENCODING=QUOTED-PRINTABLE:Erste Zeile=0D=0AZweite Zeile mit =E4 und eine sehr lange Zeile, die mit einem weichen =",
    "Zeilenumbruch fortgesetzt wird=0D=0ADritte Zeile",
    "LOCATION;ENCODING=QUOTED-PRINTABLE;CHARSET=UTF-8:Z=C3=BCrich, B=C3=BCro 5",
    "CATEGORIES:MEETING,PHONE CALL", "ATTENDEE;ROLE=ATTENDEE;STATUS=ACCEPTED;RSVP=YES:Hans Muster <hans@example.com>",
    "ATTENDEE;ROLE=ATTENDEE;STATUS=NEEDS ACTION;EXPECT=FYI:fyi@example.com",
    "DALARM:20260316T074500;PT5M;2;Time to go", "AALARM:20260316T074000;;;", "RRULE:W1 MO WE #4", "CLASS:PUBLIC", "PRIORITY:2", "STATUS:CONFIRMED",
    "END:VEVENT",
    "BEGIN:VEVENT", "DTSTART:20260401T120000Z", "DTEND:20260401T130000Z", "UID:v1-2@example.com", "SUMMARY:Daily legacy", "RRULE:D2 #5", "END:VEVENT",
    "BEGIN:VEVENT", "DTSTART:20260501T120000Z", "DTEND:20260501T130000Z", "UID:v1-3@example.com", "SUMMARY:Monthly legacy", "RRULE:MD1 1 15 #4", "END:VEVENT",
    "BEGIN:VTODO", "DUE:20260320T170000Z", "UID:v1-t1@example.com", "SUMMARY;ENCODING=QUOTED-PRINTABLE:Erledigen =3D wichtig", "STATUS:NEEDS ACTION", "END:VTODO",
    "END:VCALENDAR"]
put("vcalendar-1.0-quoted-printable.ics", text(v1))
v1_8bit = [
    "BEGIN:VCALENDAR", "VERSION:1.0", "PRODID:-//Legacy PDA//EN", "TZ:-05:00",
    "BEGIN:VEVENT", "DTSTART:20260315T080000", "DTEND:20260315T090000", "UID:v1-8bit@example.com",
    "SUMMARY:Besprechung im B\u00fcro \u20ac5", "LOCATION:Z\u00fcrich", "DESCRIPTION:Caf\u00e9 \u201cquoted\u201d", "END:VEVENT", "END:VCALENDAR"]
put("vcalendar-1.0-8bit-windows1252.ics", text(v1_8bit).encode("cp1252"))

# --- malformed / hostile ----------------------------------------------------------------------------
good = text(cal(*event("UID:trunc@example.com", "DTSTAMP:20260101T080000Z", "DTSTART:20260105T100000Z", "DTEND:20260105T110000Z",
                       "SUMMARY:Truncated subject line", "DESCRIPTION:This description is cut off somewhere", "ORGANIZER;CN=Cut Off:mailto:cut@example.com")))
put("malformed-truncated-mid-line.ics", good[:good.index("is cut off") + 4])
put("malformed-missing-end.ics", good[:good.index("END:VEVENT")])
put("malformed-stray-end.ics", good.replace("END:VEVENT", "END:VEVENT\r\nEND:VTODO\r\nEND:VEVENT"))
put("malformed-garbage-lines.ics", good.replace("SUMMARY:", "this line is not a property\r\n:starts with colon\r\nNOCOLON\r\n=;=:=\r\nSUMMARY:"))
put("malformed-no-version-prodid.ics", "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:nv@example.com\r\nDTSTART:20260105T100000Z\r\nSUMMARY:No version\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n")
put("malformed-bad-dates.ics", text(cal(*event("UID:bad-dates@example.com", "DTSTART:2026-13-45T25:61:61", "DTEND:notadate", "CREATED:00000000T000000Z", "SUMMARY:Bad dates"))))
put("malformed-event-without-wrapper.ics", "BEGIN:VEVENT\r\nUID:nowrap@example.com\r\nDTSTART:20260105T100000Z\r\nSUMMARY:No VCALENDAR wrapper\r\nEND:VEVENT\r\n")
put("malformed-unclosed-quote.ics", text(cal(*event("UID:uq@example.com", "DTSTART:20260105T100000Z", 'ATTENDEE;CN="Unclosed quote;ROLE=CHAIR:mailto:uq@example.com', "SUMMARY:After unclosed quote"))))
put("malformed-deep-nesting.ics", "BEGIN:VCALENDAR\r\n" + "BEGIN:X-LEVEL\r\n" * 500 + "SUMMARY:deep\r\n" + "END:X-LEVEL\r\n" * 500 + "BEGIN:VEVENT\r\nUID:after-deep@example.com\r\nDTSTART:20260105T100000Z\r\nSUMMARY:After deep nesting\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n")
put("malformed-binary-noise.ics", b"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:noise@example.com\r\nSUMMARY:\x00\x01\x02\xff\xfe noise \x80\x81\r\nDTSTART:20260105T100000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n")
put("malformed-empty-calendar.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//empty//EN\r\nEND:VCALENDAR\r\n")

# negatives: not calendars at all
put("not-a-calendar.txt", "This is a plain text file.\r\nNothing to see here: really.\r\n")
put("not-a-calendar-vcard.vcf", "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Some Person\r\nN:Person;Some;;;\r\nEND:VCARD\r\n")
put("not-a-calendar-random.bin", bytes((i * 131 + 17) % 256 for i in range(2048)))
put("not-a-calendar-empty.ics", b"")


def main(root):
    out = os.path.join(root, "ics", "xray-ics")
    os.makedirs(out, exist_ok=True)
    for name, data in sorted(F.items()):
        with open(os.path.join(out, name), "wb") as handle:
            handle.write(data)
    print("wrote", len(F), "fixtures to", out)


if __name__ == "__main__":
    main(sys.argv[1])
