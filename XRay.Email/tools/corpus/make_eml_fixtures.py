"""Writes the hand-crafted EML fixtures of test_documents/email/xray-email/.

Each message exercises one area of RFC 5322 / MIME parsing. The bytes are written exactly as
given (line endings included), so run this from any directory:

    python3 -I make_eml_fixtures.py <test_documents>/email/xray-email

The files are committed to curiosity-ai/test_documents; rerun only to change or add a case.
"""

import base64
import sys
from pathlib import Path


def crlf(text: str) -> bytes:
    return text.replace("\r\n", "\n").replace("\n", "\r\n").encode("ascii")


def b64(data: bytes) -> str:
    raw = base64.b64encode(data).decode("ascii")
    return "\n".join(raw[i:i + 76] for i in range(0, len(raw), 76))


# A 1x1 transparent PNG and a few bytes of a PDF: real formats, tiny.
PNG = bytes.fromhex(
    "89504e470d0a1a0a0000000d4948445200000001000000010806000000"
    "1f15c4890000000d49444154789c6360000002000154a24f5f0000000049454e44ae426082")
PDF = b"%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[]/Count 0>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n"
BINARY = bytes(range(256))


def fixtures() -> dict[str, bytes]:
    f: dict[str, bytes] = {}

    f["plain-ascii.eml"] = crlf("""\
From: Alice Example <alice@example.com>
To: Bob Example <bob@example.com>
Subject: Plain ASCII message
Date: Tue, 02 Jan 2024 03:04:05 +0000
Message-ID: <plain-ascii@example.com>
MIME-Version: 1.0
Content-Type: text/plain; charset=us-ascii
Content-Transfer-Encoding: 7bit

Hello Bob,

This is a plain text message with nothing unusual in it.

Alice
""")

    f["plain-lf-line-endings.eml"] = b"""From: alice@example.com
To: bob@example.com
Subject: Bare LF line endings
Date: Wed, 03 Jan 2024 10:00:00 +0100

Every line of this message ends in a bare LF,
as files saved on Unix often do.
"""

    f["alternative-text-html.eml"] = crlf("""\
From: "Example, Alice" <alice@example.com>
To: bob@example.com
Subject: Alternative text and HTML
Date: Thu, 04 Jan 2024 08:30:00 -0500
Message-ID: <alternative@example.com>
MIME-Version: 1.0
Content-Type: multipart/alternative; boundary="alt-boundary"

This is the preamble. It is not part of any body.

--alt-boundary
Content-Type: text/plain; charset=utf-8
Content-Transfer-Encoding: 7bit

Plain version of the message.
--alt-boundary
Content-Type: text/html; charset=utf-8
Content-Transfer-Encoding: 7bit

<html><body><p>HTML version of the <b>message</b>.</p></body></html>
--alt-boundary--

This is the epilogue. It is not part of any body either.
""")

    f["mixed-with-attachments.eml"] = crlf(f"""\
From: Alice <alice@example.com>
To: Bob <bob@example.com>, Carol <carol@example.com>
Cc: Dave <dave@example.com>
Bcc: archive@example.com
Subject: Report with attachments
Date: Fri, 05 Jan 2024 12:00:00 +0000
Message-ID: <mixed@example.com>
MIME-Version: 1.0
Content-Type: multipart/mixed; boundary="mixed-boundary"

--mixed-boundary
Content-Type: text/plain; charset=utf-8

Please find the report and the data attached.
--mixed-boundary
Content-Type: application/pdf; name="report.pdf"
Content-Disposition: attachment; filename="report.pdf"
Content-Transfer-Encoding: base64

{b64(PDF)}
--mixed-boundary
Content-Type: text/csv; charset=utf-8; name="data.csv"
Content-Disposition: attachment; filename="data.csv"

id,value
1,alpha
2,beta
--mixed-boundary
Content-Type: application/octet-stream
Content-Disposition: attachment; filename="all-bytes.bin"
Content-Transfer-Encoding: base64

{b64(BINARY)}
--mixed-boundary--
""")

    f["related-inline-image.eml"] = crlf(f"""\
From: Alice <alice@example.com>
To: Bob <bob@example.com>
Subject: Newsletter with an inline image
Date: Sat, 06 Jan 2024 09:15:00 +0000
MIME-Version: 1.0
Content-Type: multipart/mixed; boundary="outer"

--outer
Content-Type: multipart/alternative; boundary="middle"

--middle
Content-Type: text/plain; charset=utf-8

Text part of the newsletter.
--middle
Content-Type: multipart/related; boundary="inner"; type="text/html"

--inner
Content-Type: text/html; charset=utf-8
Content-Transfer-Encoding: quoted-printable

<html><body><p>Look:</p><img src=3D"cid:logo@example.com" alt=3D"logo"></bod=
y></html>
--inner
Content-Type: image/png
Content-Transfer-Encoding: base64
Content-ID: <logo@example.com>
Content-Disposition: inline; filename="logo.png"

{b64(PNG)}
--inner--
--middle--
--outer
Content-Type: text/plain; charset=utf-8; name="notes.txt"
Content-Disposition: attachment; filename="notes.txt"

Attached notes.
--outer--
""")

    inner = f"""\
From: Carol <carol@example.com>
To: Alice <alice@example.com>
Subject: Original message
Date: Mon, 01 Jan 2024 18:00:00 +0000
Message-ID: <original@example.com>
MIME-Version: 1.0
Content-Type: multipart/mixed; boundary="inner-mixed"

--inner-mixed
Content-Type: text/plain; charset=utf-8

This is the original message that was forwarded.
--inner-mixed
Content-Type: image/png; name="pixel.png"
Content-Disposition: attachment; filename="pixel.png"
Content-Transfer-Encoding: base64

{b64(PNG)}
--inner-mixed--
"""
    f["nested-rfc822.eml"] = crlf(f"""\
From: Alice <alice@example.com>
To: Bob <bob@example.com>
Subject: Fwd: Original message
Date: Tue, 02 Jan 2024 09:00:00 +0000
MIME-Version: 1.0
Content-Type: multipart/mixed; boundary="forward"

--forward
Content-Type: text/plain; charset=utf-8

Forwarding the message below.
--forward
Content-Type: message/rfc822
Content-Disposition: attachment; filename="original.eml"

{inner}--forward--
""")

    f["qp-utf8-body.eml"] = crlf("""\
From: Ren=?utf-8?Q?=C3=A9?= <rene@example.com>
To: bob@example.com
Subject: Quoted-printable UTF-8 body
Date: Sun, 07 Jan 2024 07:07:07 +0000
MIME-Version: 1.0
Content-Type: text/plain; charset="UTF-8"
Content-Transfer-Encoding: quoted-printable

Caf=C3=A9 cr=C3=A8me br=C3=BBl=C3=A9e, na=C3=AFve fa=C3=A7ade.  =20
This line is long enough that the encoder had to break it with a soft line =
break, which the decoder must remove.
Euro sign: =E2=82=AC, emoji: =F0=9F=98=80.
""")

    text = "Grüße aus Köln! Ελληνικά, русский, 日本語, עברית.\n"
    f["base64-utf8-body.eml"] = crlf(f"""\
From: alice@example.com
To: bob@example.com
Subject: Base64 UTF-8 body
Date: Mon, 08 Jan 2024 08:08:08 +0000
MIME-Version: 1.0
Content-Type: text/plain; charset=utf-8
Content-Transfer-Encoding: base64

{b64(text.encode("utf-8"))}
""")

    # "日本語のテスト件名です" split mid-character across two encoded words.
    subject = "日本語のテスト件名です".encode("utf-8")
    first, second = subject[:10], subject[10:]
    f["headers-utf8-split-words.eml"] = crlf(f"""\
From: =?UTF-8?B?{base64.b64encode('山田 太郎'.encode()).decode()}?= <taro@example.jp>
To: =?utf-8?q?Doe=2C_John?= <john@example.com>, =?utf-8?b?{base64.b64encode('Zoë'.encode()).decode()}?= <zoe@example.com>
Subject: =?UTF-8?B?{base64.b64encode(first).decode()}?=
 =?UTF-8?B?{base64.b64encode(second).decode()}?=
Date: Tue, 09 Jan 2024 09:09:09 +0900
MIME-Version: 1.0
Content-Type: text/plain; charset=utf-8
Content-Transfer-Encoding: 8bit

""") + "本文はUTF-8の8ビットです。\r\n".encode("utf-8")

    f["headers-iso-8859-1-q.eml"] = crlf("""\
From: =?ISO-8859-1?Q?J=F6rg_M=FCller?= <joerg@example.de>
To: =?iso-8859-1?q?Fran=E7ois?= <francois@example.fr>
Subject: =?ISO-8859-1?Q?=DCber_die_Gr=FC=DFe?= und =?ISO-8859-1?Q?S=FC=DFes?=
Date: Wed, 10 Jan 2024 10:10:10 +0100
MIME-Version: 1.0
Content-Type: text/plain; charset=ISO-8859-1
Content-Transfer-Encoding: quoted-printable

Sch=F6ne Gr=FC=DFe aus M=FCnchen.
""")

    russian = "Привет, мир! Это письмо в кодировке KOI8-R.\n"
    f["koi8r-body-and-subject.eml"] = crlf(f"""\
From: =?koi8-r?B?{base64.b64encode('Иван Петров'.encode('koi8-r')).decode()}?= <ivan@example.ru>
To: olga@example.ru
Subject: =?KOI8-R?B?{base64.b64encode('Тестовое письмо'.encode('koi8-r')).decode()}?=
Date: Thu, 11 Jan 2024 11:11:11 +0300
MIME-Version: 1.0
Content-Type: text/plain; charset=koi8-r
Content-Transfer-Encoding: base64

{b64(russian.encode('koi8-r'))}
""")

    f["windows-1251-qp.eml"] = crlf("""\
From: sergey@example.ru
To: olga@example.ru
Subject: =?windows-1251?Q?=CF=F0=E8=E2=E5=F2?=
Date: Thu, 11 Jan 2024 12:00:00 +0300
MIME-Version: 1.0
Content-Type: text/plain; charset=windows-1251
Content-Transfer-Encoding: quoted-printable

""") + "".join(f"={b:02X}" for b in "Добрый день.".encode("cp1251")).encode("ascii") + b"\r\n"

    chinese = "你好，这是一封简体中文邮件。\n"
    f["gb2312-html.eml"] = crlf(f"""\
From: =?GB2312?B?{base64.b64encode('王小明'.encode('gb2312')).decode()}?= <wang@example.cn>
To: li@example.cn
Subject: =?gb2312?B?{base64.b64encode('中文测试'.encode('gb2312')).decode()}?=
Date: Fri, 12 Jan 2024 12:12:12 +0800
MIME-Version: 1.0
Content-Type: text/html; charset=gb2312
Content-Transfer-Encoding: base64

{b64(('<html><body><p>' + chinese.strip() + '</p></body></html>').encode('gb2312'))}
""")

    f["iso-2022-jp.eml"] = crlf(f"""\
From: =?ISO-2022-JP?B?{base64.b64encode('鈴木'.encode('iso-2022-jp')).decode()}?= <suzuki@example.jp>
To: tanaka@example.jp
Subject: =?ISO-2022-JP?B?{base64.b64encode('会議の件'.encode('iso-2022-jp')).decode()}?=
Date: Sat, 13 Jan 2024 13:13:13 +0900
MIME-Version: 1.0
Content-Type: text/plain; charset=ISO-2022-JP
Content-Transfer-Encoding: 7bit

""") + "明日の会議は十時からです。\r\n".encode("iso-2022-jp")

    f["raw-utf8-headers.eml"] = ("""\
From: Zoë Ångström <zoe@example.com>
To: Ünal Çelik <unal@example.com>
Subject: Raw UTF-8 headers (RFC 6532): ça marche
Date: Sun, 14 Jan 2024 14:14:14 +0000
MIME-Version: 1.0
Content-Type: text/plain; charset=utf-8
Content-Transfer-Encoding: 8bit

Body in raw UTF-8: ça marche très bien.
""").replace("\n", "\r\n").encode("utf-8")

    f["rfc2231-filenames.eml"] = crlf(f"""\
From: alice@example.com
To: bob@example.com
Subject: Attachment names in every encoding
Date: Mon, 15 Jan 2024 15:15:15 +0000
MIME-Version: 1.0
Content-Type: multipart/mixed; boundary="names"

--names
Content-Type: text/plain

Four attachments with non-ASCII names.
--names
Content-Type: application/pdf
Content-Disposition: attachment; filename*=utf-8''r%C3%A9sum%C3%A9.pdf
Content-Transfer-Encoding: base64

{b64(PDF)}
--names
Content-Type: text/plain; charset=utf-8
Content-Disposition: attachment;
 filename*0*=utf-8''%E9%95%B7%E3%81%84%E3%83%95%E3%82%A1%E3%82%A4%E3%83%AB;
 filename*1*=%E5%90%8D.txt

long name in two RFC 2231 sections
--names
Content-Type: image/png; name="=?utf-8?B?{base64.b64encode('übersicht.png'.encode()).decode()}?="
Content-Disposition: attachment
Content-Transfer-Encoding: base64

{b64(PNG)}
--names
Content-Type: text/plain; name="plain; with \\"quotes\\".txt"
Content-Disposition: attachment; filename="plain; with \\"quotes\\".txt"

quoted name with a semicolon
--names--
""")

    f["address-groups-and-comments.eml"] = crlf("""\
From: "Smith, John \\"JJ\\"" <john.smith@example.com>
To: undisclosed-recipients:;
Cc: Project Team: alice@example.com, "Bob B." <bob@example.com>;, carol@example.com (Carol C.)
Reply-To: <replies@example.com>
Subject: Groups, comments and quoted names
Date: Tue, 16 Jan 2024 16:16:16 +0000
MIME-Version: 1.0
Content-Type: text/plain

Address parsing edge cases.
""")

    f["obsolete-date.eml"] = crlf("""\
From: old@example.com
To: new@example.com
Subject: Obsolete date syntax
Date: 5 Jan 99 13:04 EST (Eastern Standard Time)

Two-digit year, no day of week, no seconds, a named zone and a comment.
""")

    ics = """BEGIN:VCALENDAR\r
VERSION:2.0\r
PRODID:-//X-Ray//Test//EN\r
METHOD:REQUEST\r
BEGIN:VEVENT\r
UID:meeting-1@example.com\r
DTSTART:20240120T100000Z\r
DTEND:20240120T110000Z\r
SUMMARY:Planning meeting\r
ORGANIZER:mailto:alice@example.com\r
ATTENDEE:mailto:bob@example.com\r
END:VEVENT\r
END:VCALENDAR\r
"""
    f["calendar-invite.eml"] = crlf(f"""\
From: Alice <alice@example.com>
To: Bob <bob@example.com>
Subject: Invitation: Planning meeting
Date: Wed, 17 Jan 2024 17:17:17 +0000
MIME-Version: 1.0
Content-Type: multipart/mixed; boundary="cal-mixed"

--cal-mixed
Content-Type: multipart/alternative; boundary="cal-alt"

--cal-alt
Content-Type: text/plain; charset=utf-8

You are invited to the planning meeting on 20 January at 10:00 UTC.
--cal-alt
Content-Type: text/calendar; charset=utf-8; method=REQUEST

{ics}--cal-alt--
--cal-mixed
Content-Type: application/ics; name="invite.ics"
Content-Disposition: attachment; filename="invite.ics"
Content-Transfer-Encoding: base64

{b64(ics.encode())}
--cal-mixed--
""")

    f["no-mime-latin1-body.eml"] = b"From: legacy@example.com\r\nTo: bob@example.com\r\nSubject: No MIME headers\r\nDate: Thu, 18 Jan 2024 18:18:18 +0000\r\n\r\n" + \
        "Undeclared 8-bit Latin-1 text: déjà vu, naïve.\r\n".encode("latin-1")

    f["windows-1252-smart-quotes.eml"] = crlf("""\
From: writer@example.com
To: editor@example.com
Subject: Smart quotes in Windows-1252
Date: Fri, 19 Jan 2024 19:19:19 +0000
MIME-Version: 1.0
Content-Type: text/plain; charset="windows-1252"
Content-Transfer-Encoding: quoted-printable

=93Quoted=94 text =96 with an en dash and =80 euro.
""")

    f["multipart-missing-close.eml"] = crlf("""\
From: alice@example.com
To: bob@example.com
Subject: Multipart without a closing boundary
Date: Sat, 20 Jan 2024 20:20:20 +0000
MIME-Version: 1.0
Content-Type: multipart/mixed; boundary=simple

--simple  \t
Content-Type: text/plain

First part; its boundary line carries trailing whitespace.
--simple

Second part has no headers at all, so it is text/plain by default.
--simple
Content-Type: text/plain; name="tail.txt"
Content-Disposition: attachment; filename="tail.txt"

The message ends without a closing boundary.
""")

    f["long-folded-subject.eml"] = crlf("""\
From: alice@example.com
To: bob@example.com
Subject: This subject is long enough that it was folded across
\tseveral lines by the sending mail client, using a tab
  and then spaces
Date: Sun, 21 Jan 2024 21:21:21 +0000
MIME-Version: 1.0
Content-Type: text/plain

Folding test.
""")

    f["headers-only.eml"] = crlf("""\
From: alice@example.com
To: bob@example.com
Subject: Headers only, no body
Date: Mon, 22 Jan 2024 22:22:22 +0000
""")

    f["mbox-from-line.eml"] = crlf("""\
From alice@example.com Tue Jan 23 23:23:23 2024
Return-Path: <alice@example.com>
From: Alice <alice@example.com>
To: Bob <bob@example.com>
Subject: Message saved from an mbox file
Date: Tue, 23 Jan 2024 23:23:23 +0000
Content-Type: text/plain

The first line of this file is an mbox separator, not a header.
""")

    f["binary-transfer-encoding.eml"] = crlf("""\
From: alice@example.com
To: bob@example.com
Subject: Binary transfer encoding
Date: Wed, 24 Jan 2024 08:00:00 +0000
MIME-Version: 1.0
Content-Type: multipart/mixed; boundary="bin"

--bin
Content-Type: text/plain

An attachment sent with Content-Transfer-Encoding: binary follows.
--bin
Content-Type: application/octet-stream; name="raw.dat"
Content-Disposition: attachment; filename="raw.dat"
Content-Transfer-Encoding: binary

""") + bytes([0, 1, 2, 3, 0xFF, 0xFE, 0x80, 0x7F, 0x41, 0x42]) + crlf("""
--bin--
""")

    f["case-insensitive-headers.eml"] = crlf("""\
FROM: Alice <alice@example.com>
to: bob@example.com
SUBJECT: Header names in odd case
date: Thu, 25 Jan 2024 09:00:00 GMT
mime-version: 1.0
content-type: MULTIPART/ALTERNATIVE; BOUNDARY=CaseBoundary

--CaseBoundary
CONTENT-TYPE: TEXT/PLAIN; CHARSET=UTF-8
content-transfer-encoding: QUOTED-PRINTABLE

Upper case everywhere =E2=9C=93
--CaseBoundary
Content-Type: text/html; charset=utf-8

<p>Upper case everywhere</p>
--CaseBoundary--
""")

    f["inline-text-attachment.eml"] = crlf("""\
From: alice@example.com
To: bob@example.com
Subject: Inline text part with a file name
Date: Fri, 26 Jan 2024 10:00:00 +0000
MIME-Version: 1.0
Content-Type: multipart/mixed; boundary="inl"

--inl
Content-Type: text/plain; charset=utf-8

Main body.
--inl
Content-Type: text/plain; charset=utf-8
Content-Disposition: inline; filename="log.txt"

line 1 of the log
line 2 of the log
--inl--
""")

    return f


def main() -> None:
    target = Path(sys.argv[1])
    target.mkdir(parents=True, exist_ok=True)
    for name, data in fixtures().items():
        (target / name).write_bytes(data)
        print(f"{name}: {len(data)} bytes")


if __name__ == "__main__":
    main()
