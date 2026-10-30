#!/usr/bin/env python3
"""Writes src/XRay.ThreeD/Formats/Bim/Schemas/<schema>.txt.gz: for IFC2X3, IFC4 and IFC4X3_ADD2 the facts of the EXPRESS schema the ifcXML reader
needs to turn the XML encoding back into STEP entities: every entity's attributes in STEP order (name, derived flag, aggregation depth, kind), its
inverse attributes (and the forward attribute they mirror), the defined types, enumerations and selects. The data is read from the schemas shipped
with IfcOpenShell (pip install ifcopenshell); the facts themselves come from buildingSMART's published schemas.

    /tmp/claude-0/ezvenv/bin/python tools/fixtures/make_ifc_schema_tables.py src/XRay.ThreeD/Formats/Bim/Schemas

Lines:  E|ENTITY|SUPER|attr:flags:depth:kind[:target],...|inverse:ENTITY.attribute,...
        D|TYPE|depth|kind          (defined type: S string, I integer, R real, B boolean/logical, T select)
        N|ENUMERATION
        T|SELECT|option,option,...  (entities and defined types, selects flattened)
Kinds of an attribute: S, I, R, B, E (enumeration), X (entity, target = entity name), T (select, target = select name).
"""
import gzip, os, sys
import ifcopenshell.ifcopenshell_wrapper as w

out = sys.argv[1]
os.makedirs(out, exist_ok=True)


def resolve(t):
    """-> (depth, kind, target)"""
    depth = 0
    while type(t).__name__ == 'aggregation_type':
        depth += 1
        t = t.type_of_element()
    name = type(t).__name__
    if name == 'simple_type':
        base = t.declared_type()
        return depth, {'string': 'S', 'integer': 'I', 'real': 'R', 'number': 'R', 'boolean': 'B', 'logical': 'B', 'binary': 'S'}[base], ''
    if name == 'named_type':
        d = t.declared_type()
        dn = type(d).__name__
        if dn == 'entity':
            return depth, 'X', d.name().upper()
        if dn == 'enumeration_type':
            return depth, 'E', d.name().upper()
        if dn == 'select_type':
            return depth, 'T', d.name().upper()
        if dn == 'type_declaration':
            inner_depth, kind, target = resolve(d.declared_type())
            return depth + inner_depth, kind, target
    raise ValueError(name)


def select_options(sel, seen=None):
    seen = seen or set()
    result = []
    for o in sel.select_list():
        if o.name() in seen:
            continue
        seen.add(o.name())
        if type(o).__name__ == 'select_type':
            result += select_options(o, seen)
        else:
            result.append(o.name().upper())
    return result


for name in ('IFC2X3', 'IFC4', 'IFC4X3_ADD2'):
    s = w.schema_by_name(name)
    lines = []
    for decl in s.declarations():
        kind = type(decl).__name__
        if kind == 'entity':
            attrs = []
            derived = decl.derived()
            for i, a in enumerate(decl.all_attributes()):
                depth, k, target = resolve(a.type_of_attribute())
                attrs.append(f"{a.name()}:{'d' if derived[i] else 'e'}:{depth}:{k}" + (f':{target}' if target else ''))
            inv = [f'{x.name()}:{x.entity_reference().name().upper()}.{x.attribute_reference().name()}' for x in decl.all_inverse_attributes()]
            sup = decl.supertype().name().upper() if decl.supertype() else ''
            lines.append(f"E|{decl.name().upper()}|{sup}|{','.join(attrs)}|{','.join(inv)}")
        elif kind == 'type_declaration':
            depth, k, target = resolve(decl.declared_type())
            lines.append(f'D|{decl.name().upper()}|{depth}|{k}')
        elif kind == 'enumeration_type':
            lines.append(f'N|{decl.name().upper()}')
        elif kind == 'select_type':
            lines.append(f"T|{decl.name().upper()}|{','.join(select_options(decl))}")
    text = '\n'.join(lines) + '\n'
    with gzip.GzipFile(os.path.join(out, name.lower() + '.txt.gz'), 'wb', mtime=0) as f:
        f.write(text.encode('ascii'))
    print(name, len(lines), 'declarations', len(text), 'bytes')
