#!/usr/bin/env python3
"""Writes the Parasolid fixtures of dataset/synthetic. header.x_t, header.x_b and bare.x_t only have a meaningful header (placeholder node streams, a schema the
reader does not tabulate). disc.x_t is a complete text transmit file of the Parasolid 12 schema, written for this repository from the field lists of the public XT
format reference: a circular sheet of radius 2 (one plane face, one ring edge without vertices) with a colour attribute (0.2, 0.4, 0.6) on the face.

    python3 -I tools/fixtures/make_parasolid_fixtures.py dataset/synthetic
"""
import os, sys

out = sys.argv[1]
text = '''**ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz**************************
**PART1;
MC=XRAY-TEST;
MC_MODEL=generator;
MC_ID=0001;
OS=linux;
OS_RELEASE=6;
FRU=sdl_xray;
APPL=Hand-written header fixture;
SITE=nowhere;
USER=fixture author;
FORMAT=text;
GUISE=transmit;
KEY=part.x_t;
FILE=bracket;
DATE=2024-03-05T10:20:30;
**PART2;
SCH=SCH_2900166_28101;
USFLD_SIZE=0;
**PART3;
**END_OF_HEADER*****************************************************************
T51 : TRANSMIT FILE created by modeller version 2900166 SCH_2900166_28101 82 3 1 0 ?2147483647 82 0 91 1 0 
'''
open(os.path.join(out, 'header.x_t'), 'w', newline='\n').write(text)
binary = b'PS\x00\x00\x003: TRANSMIT FILE created by modeller version 3000000\x00\x00\x00\x11SCH_3000000_30000\x00\x00\x00\x00\x00R\x00\x00\x00\x03\x00\x02\x00\x00\x00\x00\xff\xff\x80\x04\x7f\xff\xff\xff\x00R\x00\x00\x00\x00\x00\x08\x00\x01\x00\x01'
open(os.path.join(out, 'header.x_b'), 'wb').write(binary)
open(os.path.join(out, 'bare.x_t'), 'w', newline='\n').write('T51 : TRANSMIT FILE created by modeller version 300000017 SCH_3000000_300000 82 3 1 0 ?2147483647 82 0 91 1 0 \n')

disc = ''.join(line + '\n' for line in [
    '**ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz***********',
    '**PARASOLID !"#$%&\'()*+,-./:;<=>?@[\\]^_`{|}~0123456789************',
    '**PART1;APPL=disc fixture;FORMAT=text;GUISE=transmit;DATE=2025-01-02;',
    '**PART2;SCH=SCH_1200000_12006;USFLD_SIZE=0;',
    '**PART3;',
    '**END_OF_HEADER***************************************************',
    'T51 : TRANSMIT FILE created by modeller version 120000017 SCH_1200000_120060',
    ' 12 1 12 0 2 0 0 0 0 1e3 1e-8 0 0 0 1 0 3 1 3 4 5 0 6 7 0',
    ' 70 2 0 1 0 0 4 1 20 8 8 8 1 T',
    ' 13 3 3 0 1 0 9 0 0 6 9',
    ' 50 4 11 0 9 0 0 0 +0 0 0 0 0 1 1 0 0',
    ' 31 5 10 0 7 0 0 0 +0 0 0 0 0 1 1 0 0 2',
    ' 19 6 5 0 1 0 0 3 V',
    ' 16 7 6 0 ?10 0 0 5 0 0 1',
    ' 17 10 0 11 10 10 0 12 7 0 0 +',
    ' 15 11 7 0 10 9 0',
    ' 17 12 0 0 0 0 0 10 7 0 0 -',
    ' 14 9 2 13 ?0 0 11 3 4 +0 0 0 0 3',
    ' 81 1 13 12 14 9 0 0 0 0 15',
    ' 80 1 14 0 16 8001 0 0 0 0 3 5 0 0 FFFFTFTFFFFFF2',
    ' 83 3 15 .2 .4 .6',
    ' 79 15 16 SDL/TYSA_COLOUR',
    ' 74 20 8 1 0 13 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0',
    ' 1 0',
])
open(os.path.join(out, 'disc.x_t'), 'w', newline='\n').write(disc)

# disc-embedded.x_t: the same sheet in a file that embeds its schema as edits to the 13006 base (key SCH_3501210_35102_13006, as the files of Parasolid 35 write
# them). Every node type is declared at its first use: 255 for a base layout used unchanged, an edit sequence for the BODY (a lattice pointer is inserted after
# the attribute chain; C copies a base field, I inserts one, Z ends) and a complete declaration for a type the base lacks (a made-up type 301, with a counter and
# a list of pointers, used by one node, so that the full declaration form is exercised).
lines = disc.split('\n')
end = lines.index('**END_OF_HEADER***************************************************') + 1
embedded = ''.join(l + '\n' for l in lines[:end]) + 'T51 : TRANSMIT FILE created by modeller version 350121023 SCH_3501210_35102_13006301 0'
seen = set()
for l in lines[end + 1:]:
    if not l.strip():
        continue
    t, rest = l.strip().split(' ', 1)
    if t == '1':
        embedded += ' 301 2 5 thing0 5 count0 0 1 d5 items12 1 T3 30 7 3 4 5\n 1 0\n'
        break
    if t not in seen:
        seen.add(t)
        if t == '12':
            # fields: index, highest_node_id, attributes_groups, attribute_chains; the inserted lattice pointer (0) follows the third value
            v = rest.split(' ')
            embedded += ' 12 24 CCCI7 lattice222 0 ' + 'C' * 20 + 'Z' + ' '.join(v[:4] + ['0'] + v[4:])
        else:
            # the attribute definition of the 13006 schema has a field names pointer and 14 owner flags (the Parasolid 12 line above has neither)
            embedded += ' %s 255 %s' % (t, rest.replace('FFFFTFTFFFFFF2', '0 FFFFTFTFFFFFFF2'))
    else:
        embedded += ' ' + l.strip()
    embedded += '\n'
open(os.path.join(out, 'disc-embedded.x_t'), 'w', newline='\n').write(embedded)
