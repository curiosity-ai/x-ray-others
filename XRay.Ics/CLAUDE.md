# XRay.Ics: iCalendar and vCalendar for .NET

Published as `X-Ray.Ics` (assembly and namespace `XRay.Ics`). Reads, extracts, expands and writes
iCalendar (RFC 5545, iTIP RFC 5546) and vCalendar 1.0. No package dependencies; the BCL only.

**Provenance.** Written from the specifications (RFC 5545, 5546, 6868, 7986 notes on X- properties, the
vCalendar 1.0 specification) and the shape of real exports; there is no upstream code. `LICENSE` is a
fresh MIT licence for Curiosity GmbH. The consumer that drove the API is Mosaik's file extraction helper:
it loads an appointment, reads created date, organizer, attendees, optional attendees, summary,
description, an HTML rendering and the attachments. `IcsExtractor`/`IcsExtractResult` mirror that.
Parity with the commercial library that consumer used is **unverified**: no reference output was
available. `GoldenTests` compares against reference JSON/HTML when it is dropped into
`test_documents/ics/golden/` (or `XRAY_ICS_GOLDEN`); the format is in that file's comment.

## Documentation lives in curiosity-ai/documentation

The docs for X-Ray.Ics are the `x-ray/ics/` folder of
[curiosity-ai/documentation](https://github.com/curiosity-ai/documentation), not this repository.
A change to its public API, options, defaults, behavior, limits or licence is not finished until
that page is updated; open that PR there and link it from the code PR. The root `CLAUDE.md` has
the full rule.

## Layout

| Path | What |
|---|---|
| `src/XRay.Ics/` | the public model: `Calendar`, `CalendarComponent` (`CalendarEvent`, `CalendarTodo`, `CalendarJournal`, `CalendarFreeBusy`), `CalendarTimeZone`, `CalendarAlarm`, `CalendarAddress`, `CalendarAttachment`, `IcsDateTime`, `IcsDuration`, `RecurrenceRule`, raw tree (`IcsComponent`, `IcsProperty`, `IcsParameter`) |
| `src/XRay.Ics/IcsExtractor.cs`, `IcsExtractResult.cs`, `IcsRenderer.cs` | extraction and the HTML / plain text / Markdown renderings |
| `src/XRay.Ics/Internal/` | `TextDecoder` (BOM, UTF-16/32, legacy), `ContentLineReader` (unfolding, QP soft breaks), `ContentLineParser`, `IcsReader` (tree, limits, wrapping), `IcsWriter` (folding), `IcsText` (escapes, QP, base64), `ZoneResolver`, `WindowsZones`, `DateParsing` |
| `tests/XRay.Ics.Tests/` | xUnit v3: `CorpusTests` + `Corpus/ics-manifest.json`, `ParserTests`, `ModelTests`, `RecurrenceTests`, `WriterTests`, `ExtractTests`, `CancellationTests`, `RobustnessTests`, `GoldenTests` |
| `tools/corpus/make_ics_fixtures.py` | writes the hand-crafted fixtures (`test_documents/ics/xray-ics/`) byte for byte |
| `tools/corpus/build_manifest.py` | rebuilds the expectation manifest from independent readers |

## Commands

Run from the x-ray-others root so `global.json` (Microsoft.Testing.Platform) applies.

```sh
dotnet build XRay.Ics/XRay.Ics.slnx -c Release          # must report 0 warnings
dotnet test --solution XRay.Ics/XRay.Ics.slnx -c Release
XRAY_ICS_MUTATIONS=150 dotnet test --project XRay.Ics/tests/XRay.Ics.Tests -c Release -- --filter-class "*RobustnessTests"
dotnet pack XRay.Ics/src/XRay.Ics -c Release -o artifacts
```

## Test corpus

Fixtures live in `curiosity-ai/test_documents` under `ics/`: `xray-ics/` (ours, generated), and two
permissively licensed third-party sets, `icalendar-py/` (BSD-2-Clause) and `ical-net/` (MIT), each with
its licence and a `README.md` naming the source commit. `ics/.gitattributes` marks them binary: line
endings, BOMs and code pages are part of the fixtures. Tests find the corpus through `TestDocuments`
(`XRAY_TEST_DOCUMENTS`, else the first non-empty `test_documents` above the binaries); no corpus skips,
a corpus with a missing file fails.

`Corpus/ics-manifest.json` holds the expected fields of every fixture. It is generated:

```sh
python3 -m venv /tmp/venv && /tmp/venv/bin/pip install icalendar==7.3.0 recurring-ical-events==3.8.2
python3 -I XRay.Ics/tools/corpus/make_ics_fixtures.py <test_documents>
/tmp/venv/bin/python -I XRay.Ics/tools/corpus/build_manifest.py <test_documents> XRay.Ics/tests/XRay.Ics.Tests/Corpus/ics-manifest.json
```

Parsing expectations come from python `icalendar`, recurrence expectations from
`recurring_ical_events` (which uses `dateutil.rrule`), so the reader and the rule engine are checked
against second implementations. Where the library is deliberately different, `OVERRIDES` in the script
records the expected value and the reason; read the reason before changing one. The differences are
all about leniency (quoted-printable, empty parameters, a time after `VALUE=DATE`, damaged base64,
unresolvable zones, cancelled overrides dropped from expansions) or about vCalendar 1.0, which the
oracle cannot read. `Manifest_covers_every_fixture_in_the_corpus` fails when a fixture is added without
rebuilding the manifest. Every readable fixture is also saved and reloaded (raw tree equal) and rendered.

## Rules

- Only `IcsFormatException` (no calendar at all, or a limit exceeded) and `OperationCanceledException`
  escape from loading, expanding, rendering and saving. Any other exception, a hang or a stack overflow is
  a bug; `RobustnessTests` (seeded truncations, mutations, random bytes and lines, hostile rules) guards it.
  Add a regression case when you fix one. Typed accessors never throw: unreadable values are null.
- Recursion is bounded (`IcsParseOptions.MaxDepth`; unknown deeper components are skipped), line length,
  input size, component and property counts have limits, and every recurrence loop ends (COUNT, UNTIL,
  year 9999, one million periods).
- Cancellation: every public method that does real work takes `CancellationToken cancellationToken = default`
  last, checks it per 1,024 lines (reader), per 256 periods and per instance (expansion), per component
  (render, save) and per 8 KiB block (base64), and throws `OperationCanceledException`, never wrapped.
- Streams: `Save(Stream)`, `ToHtml(Stream)`, `CalendarAttachment.WriteTo(Stream)` write without buffering the
  whole output; `ToBytes`, `ToIcsString` and `ToHtmlBytes` are thin wrappers over them.
- The typed model is a view over the raw tree: setters write to `Raw`, getters parse on demand, and no
  property is dropped by a load and a save. The wire form of a value (TEXT escapes in place) is what
  `IcsProperty.Value` holds; use `TextValue` for the unescaped text.
- Times: DTSTART is always the first instance of a recurrence set; the rule alone only yields matching
  instances (dateutil's behaviour). The window of `Expand` selects instances that overlap
  [from, to). Floating times and dates are read in the `floatingZone` argument (UTC by default).
- The HTML rendering is deterministic: no clock, no culture (invariant), UTF-8, markup characters
  escaped, no script, no external reference. Keep it that way; golden comparisons depend on it.
- Zero build warnings. Public API changes are additive. No vendor names in code, comments or docs.

## Known gaps

`VALUE=PERIOD` RDATEs contribute only their start; `RANGE=THISANDFUTURE` overrides are not applied;
`VAVAILABILITY`/`VPOLL` and other RFC 7953/9073 components are kept in the raw tree but have no typed
view; `X-WR-TIMEZONE` is exposed (`DefaultTimeZoneId`) but not applied to floating times; Windows zone
names are mapped from a built-in table of the common ones, not from CLDR data; the rendering is English.
