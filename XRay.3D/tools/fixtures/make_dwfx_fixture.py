#!/usr/bin/env python3
"""Writes dataset/synthetic/drawing.dwfx: a DWFx package (XPS parts plus a DWF manifest) with a stroked rectangle, a filled triangle, a cubic curve and glyphs
under a canvas transform. Hand-written from the XPS (ECMA-388) schema; the tests check the exact geometry.

    python3 -I tools/fixtures/make_dwfx_fixture.py dataset/synthetic
"""
import os, sys, zipfile

out = sys.argv[1]
page = '''<?xml version="1.0" encoding="UTF-8"?>
<FixedPage xmlns="http://schemas.microsoft.com/xps/2005/06" Width="192" Height="96" xml:lang="en-US">
  <Path Data="M 0,0 L 96,0 96,48 0,48 Z" Stroke="#FF0000FF" StrokeThickness="1" />
  <Canvas RenderTransform="1,0,0,1,96,0">
    <Path Data="M 0,0 L 48,0 L 24,48 Z" Fill="#FF00AA00" />
    <Path Data="M 0,96 C 10,86 20,86 30,96" Stroke="#FF000000" />
    <Glyphs OriginX="4" OriginY="90" FontRenderingEmSize="12" UnicodeString="Hello DWFx" Fill="#FF000000" FontUri="/Resources/Fonts/a.odttf" />
  </Canvas>
</FixedPage>'''
files = {
    '[Content_Types].xml': '<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="fpage" ContentType="application/vnd.ms-package.xps-fixedpage+xml"/><Default Extension="xml" ContentType="application/xml"/></Types>',
    'manifest.xml': '<?xml version="1.0" encoding="UTF-8"?><dwf:Manifest xmlns:dwf="DWF-Manifest:6.0" version="6.0"><dwf:Sections><dwf:Section type="com.autodesk.dwf.ePlot" title="Synthetic sheet" name="sheet1" href="Documents/1/Pages/1.fpage"/></dwf:Sections></dwf:Manifest>',
    'FixedDocumentSequence.fdseq': '<FixedDocumentSequence xmlns="http://schemas.microsoft.com/xps/2005/06"><DocumentReference Source="/Documents/1/FixedDocument.fdoc"/></FixedDocumentSequence>',
    'Documents/1/FixedDocument.fdoc': '<FixedDocument xmlns="http://schemas.microsoft.com/xps/2005/06"><PageContent Source="Pages/1.fpage"/></FixedDocument>',
    'Documents/1/Pages/1.fpage': page,
}
with zipfile.ZipFile(os.path.join(out, 'drawing.dwfx'), 'w', zipfile.ZIP_DEFLATED) as z:
    for name, text in files.items():
        info = zipfile.ZipInfo(name, (2020, 1, 1, 0, 0, 0))
        z.writestr(info, text)
