# XRay.Email: MSG, EML and CFB for .NET

Published as `X-Ray.Email` (assembly and namespaces `XRay.Email`). Reads and writes Outlook `.msg`
files, Compound File Binary containers and EML/MIME messages, and converts between MSG and EML.
No package dependencies; the BCL only.

**Provenance.** Derived from an MIT-licensed open-source C# MSG/EML toolkit; see `LICENSE`, which
is kept verbatim and packed into the nupkg. Do not name that toolkit's vendor anywhere you write
(code, docs, metadata, commits); `LICENSE` is the only file that does. Since the port, many
parsing and writing bugs were fixed (listed below), so the original is no longer a behavioural
reference: the specifications are (MS-CFB, MS-OXMSG, MS-OXCMSG, MS-OXRTFCP, RFC 5322, 2045-2047,
2231, 6532).

## Layout

| Path | What |
|---|---|
| `src/XRay.Email/Cfb/` | `CfbReader`/`CfbWriter`, `CfbDocument`/`CfbStorage`/`CfbStream`, directory model (`XRay.Email.Cfb`) |
| `src/XRay.Email/Msg/` | `MapiMessage` (partials: `.Codec` property streams, `.Helpers` storages), `MsgReader`/`MsgWriter`/`MsgDocument`, `MapiAttachment`, `MapiRecipient`, `CompressedRtf` (`XRay.Email.Msg`) |
| `src/XRay.Email/Msg/Mime/` | Internal MIME stack: `MimeReader`, `HeaderParser`, `AddressParser`, `MultipartParser`, transfer encodings, `CharsetDecoder`, `MimeWriter`, `EmlMessageMapper` (MIME model <-> `MapiMessage`) |
| `tests/XRay.Email.Tests/` | xUnit v3. `CorpusTests` + `Corpus/email-manifest.json`, `RobustnessTests`, `MimeParsingTests`, `MsgFormatTests`, plus the ported original tests |
| `tools/XRay.Email.CorpusGen/` | Writes the generated MSG fixtures (`test_documents/email/xray-email/msg/`) |
| `tools/corpus/make_eml_fixtures.py` | Writes the hand-crafted EML fixtures (`test_documents/email/xray-email/`) |
| `tools/corpus/build_manifest.py` | Rebuilds the expectation manifest from independent readers |
| `examples/` | `CreateMsgAndEml`, `MsgReaderDump` (CFB/MSG structure), `MsgSummary` |

## Commands

Run from the x-ray-others root so `global.json` (Microsoft.Testing.Platform) applies.

```sh
dotnet build XRay.Email/XRay.Email.slnx -c Release          # must report 0 warnings
dotnet test --solution XRay.Email/XRay.Email.slnx -c Release
XRAY_EMAIL_MUTATIONS=300 dotnet test --project XRay.Email/tests/XRay.Email.Tests -c Release -- --filter-class "*RobustnessTests"
dotnet pack XRay.Email/src/XRay.Email -c Release -o artifacts
```

## Test corpus

Fixtures live in `curiosity-ai/test_documents` under `email/` (upstream xberg files),
`email/msgreader/` and `email/mimekit/` (MIT third-party sets, each with `LICENSE` and
`README.md`), and `email/xray-email/` (ours). Tests find the corpus through `TestDocuments`:
`XRAY_TEST_DOCUMENTS`, else the first non-empty `test_documents` directory above the binaries.
No corpus: corpus tests skip. Corpus present but a file missing: they fail.

`Corpus/email-manifest.json` holds the expected fields of every fixture (subject, sender,
recipients, Message-ID, date, body and HTML length and prefix, attachments). It is generated, not
hand-written:

```sh
python3 -m venv /tmp/venv && /tmp/venv/bin/pip install extract_msg==0.56.1
cd /tmp && /tmp/venv/bin/python -I <x-ray-others>/XRay.Email/tools/corpus/build_manifest.py <test_documents> <x-ray-others>/XRay.Email/tests/XRay.Email.Tests/Corpus/email-manifest.json
```

EML expectations come from Python's `email` package and MSG ones from `extract_msg`, so decoding is
checked against a second implementation. Where the library is deliberately more lenient than the
oracle, `OVERRIDES` in the script records the expected value and the reason; review the reason
before changing one. `Manifest_covers_every_fixture_in_the_corpus` fails when a fixture is added
without rebuilding the manifest. Each fixture is also round-tripped MSG->MSG, EML->EML, EML->MSG
(strict reload) and MSG->EML.

## Rules

- Malformed MSG input raises `CfbException` or `MsgException`, or is read leniently with an entry
  in `ValidationIssues`; any other exception type, a hang or a stack overflow is a bug. EML
  parsing never throws. `RobustnessTests` guards both; add a regression case when you fix one.
- Recursion over file structure is bounded: `MimeReader.MaxDepth` for multipart and
  `message/rfc822` nesting, a depth limit and ancestor check in `MsgDocument.BuildStorage`, a
  visited set in `CfbReader.IterTree`.
- Body/attachment policy for EML: the first non-attachment `text/plain` leaf is `Body`, the first
  non-attachment `text/html` leaf is `HtmlBody`, `message/rfc822` becomes an embedded message,
  every other leaf is an attachment. A part is an attachment when its disposition says so or it
  has a file name. `build_manifest.py` mirrors this; change both together.
- Header values keep two forms: `MimeHeader.RawValue` (wire) and `Value` (encoded words decoded).
  Structured headers (addresses, Content-Type, Content-Disposition) are parsed from `RawValue`;
  the writer emits `RawValue`, so code that builds headers must put the encoded form there.
- Writing: attachments are base64 (text included, to keep bytes exact), embedded messages are
  `7bit`/`8bit` (RFC 2046 5.2.1), boundaries come from one `MimeBoundaryFactory` per top-level
  message, header lines fold at 78 characters.
- MSG writing follows MS-OXMSG: 32-byte header for the top-level property stream, 24-byte for
  embedded messages (older 32-byte embedded headers are still read), the three named-property
  streams always present, PtypString8 in the message code page (PR_MESSAGE_CODEPAGE, then
  PR_INTERNET_CPID, else Windows-1252).
- Zero build warnings. Public API changes are additive; keep the `XRay.Email.Cfb` and
  `XRay.Email.Msg` namespaces stable.
- Do not commit generated fixtures without regenerating the manifest and running the tests.

## Fixed since the port

Embedded-message header size (24 bytes) on read and write; missing named-property streams; MSG
trailing bytes and missing `__nameid` tolerated in lenient mode; binary PR_HTML decoded;
PtypString8 code page; setters no longer shadowed by a loaded PtypString8 value;
`FromMsgDocument(ToMsgDocument())` lost every property; multi-valued string/binary properties were
written empty; directory-name overrun, cycles and integer overflows in the CFB reader; FILETIME
overflow. MIME: adjacent encoded words, RFC 2231, address groups/comments/encoded commas, RFC 5322
dates, raw UTF-8 headers, BOM, folds without whitespace, boundary padding and missing close
boundary, one CRLF per boundary (the writer added a line to every part), headerless parts, QP
trailing whitespace, lenient base64, nested messages sharing boundaries, whole-header encoded
words that broke Content-Type, base64 `message/rfc822`, `Date` written as `+00:00` instead of `+0000`, unbounded nesting.
