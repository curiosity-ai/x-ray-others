#!/usr/bin/env python3
"""Writes the Parasolid header fixtures of dataset/synthetic. Only the header is meaningful: the node streams are placeholders, since the
field layouts of a Parasolid schema are not public (the reader decodes the header and says so for the geometry).

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
