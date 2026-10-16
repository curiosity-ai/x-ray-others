"""Builds tests/XRay.Email.Tests/Corpus/email-manifest.json from independent readers.

EML files are read with the Python standard library (`email`, policy.default); MSG files with
`extract_msg`. Neither shares code with XRay.Email, so the manifest checks decoding (charsets,
transfer encodings, RFC 2047/2231, CFB and MAPI property streams) against a second
implementation. What counts as the body and what counts as an attachment is a policy choice, so
the EML walk below follows XRay.Email's documented policy (see CLAUDE.md) rather than Python's.

    python3 -m venv .venv && .venv/bin/pip install extract_msg==0.56.1
    .venv/bin/python -I build_manifest.py <test_documents> <manifest.json>

Run it with `-I` from outside the corpus: the fixtures are untrusted input. Differences where
XRay.Email is deliberately more lenient than the oracle are listed in OVERRIDES with a reason.
"""

import email
import email.header
import email.message
import email.policy
import email.utils
import json
import re
import sys
from datetime import timezone
from pathlib import Path

FOLDERS = ["email", "email/msgreader", "email/mimekit", "email/xray-email", "email/xray-email/msg"]
PREFIX_LENGTH = 160

# Fixtures the oracle cannot read, or reads differently on purpose. Each value replaces fields of the
# generated entry. Keep the reason next to every entry.
OVERRIDES: dict[str, dict] = {
    # A 12-byte text file named .msg: not a compound file at all.
    "email/bad_outlook.msg": {"error": "CfbException"},
    # Both have a top-level property stream with 4 trailing bytes and a FAT entry that olefile
    # rejects. XRay.Email reads them in lenient mode and reports a validation issue instead.
    "email/simple_msg_alt.msg": {"_oracle": "simple_msg.msg", "validationIssues": True},
    "email/msg_with_attachments_alt.msg": {"_oracle": "msg_with_png_attachment.msg", "validationIssues": True},
    "email/simple_msg.msg": {"validationIssues": True},
    "email/msg_with_png_attachment.msg": {"validationIssues": True},
    # "Ren=?utf-8?Q?=C3=A9?=" puts an encoded word inside an atom, which RFC 2047 forbids and Python
    # leaves alone. XRay.Email decodes it, as mail clients do.
    "email/xray-email/qp-utf8-body.eml": {"fromName": "René"},
    # Undeclared 8-bit text: Python substitutes U+FFFD; XRay.Email falls back to Windows-1252.
    "email/xray-email/no-mime-latin1-body.eml": {"bodyPrefix": "Undeclared 8-bit Latin-1 text: déjà vu, naïve."},
    # "From: jang.abcdef@xyzlinu <jang.abcdef@xyzlinux12345678.it>": the angle address is the
    # address; Python takes the unquoted display name. Python also stops parsing the headers early
    # (at an over-long header) and reads the rest as the body; the real body is "Test headers".
    "email/mimekit/issue358.eml": {"fromAddress": "jang.abcdef@xyzlinux12345678.it", "bodyLength": 12, "bodyPrefix": "Test headers", "htmlLength": 451},
    # "Content-Type: application-x-gzip" has no subtype; Python replaces it with text/plain, while
    # XRay.Email keeps what the sender wrote.
    "email/mimekit/missing-subtype.eml": {"attachments": [{"filename": "document.xml.gz", "size": 15, "contentType": "application-x-gzip"}]},
    # The PDF's base64 is cut off and ends in junk. Python then returns the undecoded text (550
    # bytes); XRay.Email decodes the 411 bytes that are there.
    "email/mailgun_pdf_attachment.eml": {"attachments": [{"filename": "test.pdf", "size": 411, "contentType": "application/pdf"}]},
    # Genuine anomalies that the lenient reader reports: four bytes after the last property entry,
    # and a header that declares two attachments where the file stores none.
    "email/msgreader/EmailWithAttachments.msg": {"validationIssues": True},
    "email/msgreader/sender_not_found_from_exchange.msg": {"validationIssues": True},
    # "Wed, 20 Sep 1995 00:19:00 (EDT) -0400": Python loses the zone after the comment.
    "email/mimekit/disposition-notification.eml": {"date": "1995-09-20T04:19:00Z"},
    # An empty multipart/alternative has no parts; Python reports it as a leaf part.
    "email/mimekit/empty-multipart.eml": {"attachments": []},
    # The second To entry is an encoded word with no address; Python keeps a fragment of it.
    "email/mimekit/stack-overflow.eml": {"to": ["test@test.com"]},
    # The Subject is folded without leading whitespace; Python ends the header block there and reads
    # the remaining headers as the body. XRay.Email treats the lines as continuations, as mail
    # clients (and MsgReader, whose test file this is) do.
    "email/msgreader/UTF-8_Test.eml": {
        "subject": "【要確認】●普通自転車（ノーパンクタイヤ） 再予約通知 2023/11/29 12:00 - 13:00",
        "to": ["test@example.com"],
        "bodyLength": 28,
        "bodyPrefix": "メッセージです。通常通りパースされることを確認済みです。",
    },
    # The file starts with a UTF-8 byte order mark, so Python reads no headers at all.
    "email/mimekit/feedback-report.eml": {"_rebuild_without_bom": True},
}


def normalize(text):
    if text is None:
        return None
    text = text.replace("\r\n", "\n").replace("\r", "\n").rstrip()
    return text


def text_fields(prefix, text):
    text = normalize(text)
    if not text:
        return {prefix + "Length": 0}
    return {prefix + "Length": len(text), prefix + "Prefix": text[:PREFIX_LENGTH]}


def utc_iso(value):
    if value is None:
        return None
    if value.tzinfo is None:
        value = value.replace(tzinfo=timezone.utc)
    return value.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


# ---------------------------------------------------------------- EML


def eml_entry(path: Path, strip_bom: bool = False) -> dict:
    data = path.read_bytes()
    if strip_bom and data.startswith(b"\xef\xbb\xbf"):
        data = data[3:]
    message = email.message_from_bytes(data, policy=email.policy.default)
    entry = {"format": "eml"}
    entry.update(eml_message_fields(message))
    return entry


def header_text(message, name):
    value = message.get(name)
    return None if value is None else str(value)


def mailboxes(message, name):
    result = []
    for header in message.get_all(name) or []:
        for mailbox in getattr(header, "addresses", []) or []:
            if mailbox.addr_spec and mailbox.addr_spec != "<>":
                result.append({"name": mailbox.display_name or None, "address": mailbox.addr_spec})
    return result


def eml_message_fields(message) -> dict:
    fields = {}
    subject = header_text(message, "subject")
    fields["subject"] = subject
    senders = mailboxes(message, "from") or mailboxes(message, "sender")
    fields["fromAddress"] = senders[0]["address"] if senders else None
    fields["fromName"] = senders[0]["name"] if senders else None
    for kind in ("to", "cc", "bcc"):
        fields[kind] = [m["address"] for m in mailboxes(message, kind)]
    message_id = header_text(message, "message-id")
    fields["messageId"] = message_id.strip() if message_id else None
    date = None
    if message.get("date") is not None:
        try:
            date = email.utils.parsedate_to_datetime(str(message["date"]))
        except (TypeError, ValueError):
            date = None
    fields["date"] = utc_iso(date)

    state = {"body": None, "html": None, "attachments": []}
    walk(message, state)
    fields.update(text_fields("body", state["body"]))
    fields.update(text_fields("html", state["html"]))
    fields["attachments"] = state["attachments"]
    return fields


def is_attachment(part) -> bool:
    disposition = (part.get_content_disposition() or "").lower()
    return disposition == "attachment" or bool(part_filename(part))


def part_filename(part):
    name = part.get_filename()
    if not name:
        params = dict(part.get_params() or [])
        name = params.get("name")
        if isinstance(name, tuple):
            name = email.utils.collapse_rfc2231_value(name)
        if name:
            name = str(email.header.make_header(email.header.decode_header(name)))
    return name.strip() if name else None


def part_text(part):
    try:
        return part.get_content()
    except (LookupError, KeyError):
        payload = part.get_payload(decode=True) or b""
        return payload.decode("utf-8", errors="replace")


def walk(part, state):
    if part.is_multipart() and part.get_content_maintype() == "multipart":
        for child in part.iter_parts():
            walk(child, state)
        return

    media = part.get_content_type()
    if media == "text/plain" and not is_attachment(part) and not state["body"]:
        state["body"] = part_text(part)
        return
    if media == "text/html" and not is_attachment(part) and not state["html"]:
        state["html"] = part_text(part)
        return
    if media == "message/rfc822":
        inner = part.get_payload()
        inner = inner[0] if isinstance(inner, list) and inner else inner
        if isinstance(inner, email.message.Message):
            state["attachments"].append({"filename": part_filename(part), "embeddedSubject": header_text(inner, "subject")})
            return
    if media.startswith("message/"):
        # Python parses message/delivery-status and similar into sub-objects and offers no decoded
        # payload to measure; only the name and type are compared.
        state["attachments"].append({"filename": part_filename(part), "contentType": media})
        return
    payload = part.get_payload(decode=True) or b""
    state["attachments"].append({"filename": part_filename(part), "size": len(payload), "contentType": media})


# ---------------------------------------------------------------- MSG


def msg_entry(path: Path) -> dict:
    import extract_msg

    message = extract_msg.openMsg(str(path), strict=False)
    try:
        return {"format": "msg", **msg_message_fields(message)}
    finally:
        message.close()


def sender_address(message):
    """The sender's SMTP address: PR_SENDER_EMAIL_ADDRESS when its type is SMTP, then
    PR_SENDER_SMTP_ADDRESS, PR_SENT_REPRESENTING_SMTP_ADDRESS, and the From transport header."""
    address = message.getStringStream("__substg1.0_0C1F")
    address_type = message.getStringStream("__substg1.0_0C1E")
    if address and "@" in address and (not address_type or address_type.upper() == "SMTP"):
        return address
    for name in ("__substg1.0_5D01", "__substg1.0_5D02"):
        value = message.getStringStream(name)
        if value:
            return value
    headers = message.getStringStream("__substg1.0_007D")
    if headers:
        parsed = email.message_from_string(headers, policy=email.policy.default)
        senders = mailboxes(parsed, "from")
        if senders:
            return senders[0]["address"]
    return address if address and "@" in address else None


def html_encoding(message):
    import codecs

    for tag in ("3FDE0003", "3FFD0003"):
        value = message.getPropertyVal(tag) if hasattr(message, "getPropertyVal") else None
        if isinstance(value, int) and value not in (1200, 1201):
            name = "utf-8" if value == 65001 else f"cp{value}"
            try:
                codecs.lookup(name)
                return name
            except LookupError:
                pass
    return "utf-8"


def msg_message_fields(message) -> dict:
    fields = {"subject": message.subject or None, "fromAddress": sender_address(message)}
    recipients = {1: [], 2: [], 3: []}
    for recipient in message.recipients:
        if recipient.email:
            recipients.setdefault(int(recipient.type), []).append(recipient.email)
    fields["to"], fields["cc"], fields["bcc"] = recipients[1], recipients[2], recipients[3]
    fields["messageId"] = message.messageId.strip() if message.messageId else None
    fields["date"] = utc_iso(message.receivedTime)
    fields.update(text_fields("body", message.body))
    # Only a stored PR_HTML counts; extract_msg would otherwise derive HTML from the RTF body.
    html = message.getStringStream("__substg1.0_1013")
    if html is None and message.exists("__substg1.0_10130102"):
        html = message.htmlBody
    if isinstance(html, bytes):
        html = html.decode(html_encoding(message), errors="replace")
    html = html.lstrip("\ufeff").rstrip("\0") if html else None
    fields["htmlLength"] = len(normalize(html)) if html else 0
    attachments = []
    for attachment in message.attachments:
        name = attachment.longFilename or attachment.shortFilename
        data = attachment.data
        if hasattr(data, "subject"):
            attachments.append({"filename": name, "embeddedSubject": data.subject or None})
        else:
            attachments.append({"filename": name, "size": len(data) if data is not None else 0})
    fields["attachments"] = attachments
    return fields


# ---------------------------------------------------------------- main


def fix_surrogates(value):
    """Python keeps raw 8-bit header bytes as surrogate escapes; read them as UTF-8 (RFC 6532),
    falling back to ISO-8859-1, which is what XRay.Email does."""
    if isinstance(value, dict):
        return {k: fix_surrogates(v) for k, v in value.items()}
    if isinstance(value, list):
        return [fix_surrogates(v) for v in value]
    if isinstance(value, str) and re.search("[\udc80-\udcff]", value):
        raw = value.encode("utf-8", "surrogateescape")
        try:
            return raw.decode("utf-8")
        except UnicodeDecodeError:
            return raw.decode("latin-1")
    return value



def main() -> None:
    corpus = Path(sys.argv[1])
    output = Path(sys.argv[2])
    entries = {}
    for folder in FOLDERS:
        for path in sorted((corpus / folder).iterdir()):
            if path.suffix.lower() not in (".eml", ".msg") or not path.is_file():
                continue
            key = f"{folder}/{path.name}"
            override = OVERRIDES.get(key, {})
            if "error" in override:
                entries[key] = {"format": path.suffix[1:].lower(), "error": override["error"]}
                continue
            source = path.with_name(override["_oracle"]) if "_oracle" in override else path
            if path.suffix.lower() == ".eml":
                entry = eml_entry(source, strip_bom=override.get("_rebuild_without_bom", False))
            else:
                entry = msg_entry(source)
            entry.update({k: v for k, v in override.items() if not k.startswith("_")})
            entries[key] = entry
    output.parent.mkdir(parents=True, exist_ok=True)
    entries = {key: fix_surrogates(entry) for key, entry in entries.items()}
    output.write_text(json.dumps(entries, indent=1, ensure_ascii=False, sort_keys=True) + "\n", encoding="utf-8")
    print(f"{len(entries)} entries written to {output}")


if __name__ == "__main__":
    main()
