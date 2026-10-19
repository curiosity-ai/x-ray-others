namespace XRay.OneNote.Internal.Html;

/// <summary>The style sheet shared by every HTML document the writer produces. Class names are part of the output contract.</summary>
internal static class HtmlStyle
{
    public const string Css = """
        .on-body{margin:0;padding:16px;background:#fff;color:#000;font-family:Calibri,"Segoe UI",Arial,sans-serif;font-size:11pt;line-height:1.35}
        .on-page{position:relative;max-width:1100px;margin:0 auto 32px auto}
        .on-section-title{font-size:24pt;margin:0 0 16px 0}
        .on-title{margin:0 0 16px 0;padding-left:48px}
        .on-title-text{font-size:20pt;font-weight:normal;margin:0}
        .on-title-meta{margin:4px 0 0 0;font-size:9pt;color:#767676}
        .on-title-date,.on-title-time{white-space:pre-wrap}
        .on-canvas{position:relative}
        .on-outline{box-sizing:border-box;margin-bottom:8px}
        .on-oe{margin:0}
        .on-children{margin-left:24px}
        .on-p{margin:0;white-space:pre-wrap;overflow-wrap:anywhere}
        h1.on-p,h2.on-p,h3.on-p,h4.on-p,h5.on-p,h6.on-p{font-size:inherit;font-weight:inherit}
        pre.on-p{font-family:inherit}
        blockquote.on-p{margin:0 0 0 2em}
        .on-align-center{text-align:center}
        .on-align-right{text-align:right}
        .on-marker{display:inline-block;min-width:1.5em;margin-right:.25em}
        .on-hidden{display:none}
        .on-hidden.on-hidden-shown{display:inline}
        .on-all-hidden{display:none}
        .on-link{color:#0563c1;text-decoration:underline}
        .on-tags{display:inline}
        .on-tag{display:inline-block;margin-right:.3em;font-weight:bold;color:#e88c2e}
        .on-tag-todo{color:#1e6fd0}
        .on-tag-star{color:#e88c2e}
        .on-tag-question{color:#a352d1}
        .on-tag-exclamation,.on-tag-flag{color:#d92e29}
        .on-tag-completed{color:#33942e}
        .on-tag-disabled{color:#999}
        .on-table{border-collapse:collapse;margin:4px 0}
        .on-table.on-borders>tbody>tr>td{border:1px solid #8c8c8c}
        .on-cell{vertical-align:top;padding:2px 6px}
        .on-image{margin:4px 0}
        .on-image.on-align-center{text-align:center}
        .on-image.on-align-right{text-align:right}
        .on-img{max-width:100%;height:auto}
        .on-image-missing,.on-image-skipped{display:inline-block;padding:2px 6px;border:1px dashed #8c8c8c;color:#767676}
        .on-file{margin:4px 0}
        .on-file-link,.on-file-name{font-weight:bold}
        .on-file-size{color:#767676;font-size:9pt}
        """;
}
