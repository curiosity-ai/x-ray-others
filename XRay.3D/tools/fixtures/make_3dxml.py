#!/usr/bin/env python3
"""Writes dataset/synthetic/assembly.3dxml (a ZIP package) and dataset/synthetic/structure.3dxml (a product structure alone) (CC0).
The package: an assembly "Rig" holding two placements of a part "Bracket" (one rotated 90 degrees about Z) and a sub-assembly "Arm" with one more;
the part's representation is a unit cube as triangles, a strip and a fan in two colours, plus a coarser level of detail that must be ignored.

    python3 tools/fixtures/make_3dxml.py dataset/synthetic
"""
import os, sys, zipfile

out = sys.argv[1]
NS = 'xmlns="http://www.3ds.com/xsd/3DXML" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xlink="http://www.w3.org/1999/xlink"'
structure = '''<?xml version="1.0" encoding="utf-8" ?>
<Model_3dxml %s>
	<Header>
		<SchemaVersion>4.3</SchemaVersion>
		<Title>Rig</Title>
		<Author>fixture</Author>
		<Generator>make_3dxml.py</Generator>
		<Created>2026-01-01</Created>
	</Header>
	<ProductStructure root="1">
		<Reference3D xsi:type="Reference3DType" id="1" name="Rig"><PLM_ExternalID>RIG-001</PLM_ExternalID><V_description>Test rig</V_description></Reference3D>
		<Reference3D xsi:type="Reference3DType" id="2" name="Bracket"><PLM_ExternalID>BRK-001</PLM_ExternalID></Reference3D>
		<Reference3D xsi:type="Reference3DType" id="3" name="Arm"/>
		<ReferenceRep xsi:type="ReferenceRepType" id="4" name="Bracket_Rep" format="TESSELLATED" version="1.2" associatedFile="urn:3DXML:Bracket.3DRep"><V_usage>3DShape</V_usage></ReferenceRep>
		<InstanceRep xsi:type="InstanceRepType" id="5" name="Bracket_InstanceRep"><IsAggregatedBy>2</IsAggregatedBy><IsInstanceOf>4</IsInstanceOf></InstanceRep>
		<Instance3D xsi:type="Instance3DType" id="6" name="Bracket.1"><IsAggregatedBy>1</IsAggregatedBy><IsInstanceOf>2</IsInstanceOf><RelativeMatrix>1 0 0 0 1 0 0 0 1 10 0 0</RelativeMatrix></Instance3D>
		<Instance3D xsi:type="Instance3DType" id="7" name="Bracket.2"><IsAggregatedBy>1</IsAggregatedBy><IsInstanceOf>2</IsInstanceOf><RelativeMatrix>0 1 0 -1 0 0 0 0 1 0 20 0</RelativeMatrix></Instance3D>
		<Instance3D xsi:type="Instance3DType" id="8" name="Arm.1"><IsAggregatedBy>1</IsAggregatedBy><IsInstanceOf>3</IsInstanceOf><RelativeMatrix>1 0 0 0 1 0 0 0 1 0 0 30</RelativeMatrix></Instance3D>
		<Instance3D xsi:type="Instance3DType" id="9" name="Bracket.3"><IsAggregatedBy>3</IsAggregatedBy><IsInstanceOf>2</IsInstanceOf><RelativeMatrix>1 0 0 0 1 0 0 0 1 5 5 5</RelativeMatrix></Instance3D>
	</ProductStructure>
</Model_3dxml>
''' % NS

cube = [(0,0,0),(1,0,0),(1,1,0),(0,1,0),(0,0,1),(1,0,1),(1,1,1),(0,1,1)]
pos = ','.join('%g %g %g' % v for v in cube)
nrm = ','.join('0 0 1' for _ in cube)
rep = '''<?xml version="1.0" encoding="utf-8" ?>
<XMLRepresentation version="1.2" %s>
	<Root xsi:type="BagRepType">
		<Rep xsi:type="BagRepType" id="2">
			<Rep xsi:type="PolygonalRepType" id="3" accuracy="0.2" solid="1">
				<PolygonalLOD accuracy="9">
					<Faces><Face triangles="0 1 2"><SurfaceAttributes><Color xsi:type="RGBAColorType" red="0" green="1" blue="0" alpha="1"/></SurfaceAttributes></Face></Faces>
				</PolygonalLOD>
				<Faces>
					<Face triangles="0 3 2 0 2 1 4 5 6 4 6 7" fans="0 1 5 4,3 7 6 2"><SurfaceAttributes><Color xsi:type="RGBAColorType" red="1" green="0" blue="0" alpha="1"/></SurfaceAttributes></Face>
					<Face strips="0 4 3 7 2 6 1 5"><SurfaceAttributes><Color xsi:type="RGBAColorType" red="0" green="0" blue="1" alpha="0.5"/></SurfaceAttributes></Face>
				</Faces>
				<Edges><Polyline vertices="0 0 0,1 0 0,1 1 0"/></Edges>
				<VertexBuffer><Positions>%s</Positions><Normals>%s</Normals></VertexBuffer>
			</Rep>
		</Rep>
	</Root>
</XMLRepresentation>
''' % (NS, pos, nrm)
manifest = '<?xml version="1.0" encoding="utf-8" ?>\n<Manifest xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="Manifest.xsd">\n\t<Root>Rig.3dxml</Root>\n</Manifest>\n'

with zipfile.ZipFile(os.path.join(out, 'assembly.3dxml'), 'w', zipfile.ZIP_DEFLATED) as z:
    for name, text in [('Manifest.xml', manifest), ('Rig.3dxml', structure), ('Bracket.3DRep', rep)]:
        zi = zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0)); zi.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(zi, text)
open(os.path.join(out, 'structure.3dxml'), 'w', newline='\n').write(structure)
