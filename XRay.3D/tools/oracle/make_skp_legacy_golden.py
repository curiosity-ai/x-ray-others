"""Regenerates tests/XRay.ThreeD.Tests/Corpus/skp-legacy-geometry-golden.json: triangle count, vertex count and bounds of the real
SketchUp 2013 to 2020 files, from an independent reader (the Python openskp package, run only as a reader; its code is LGPL and is not used
in the library).
Licence situation: no openskp code is copied, vendored or ported here. The package is imported from an external directory the operator
supplies (OPENSKP_PY, never inside this repository), exactly as a command line reader would be called; only its output (dictionaries of
vertices, edges and loops) is consumed, and the output of this script is facts (counts and bounds) about files. The scene aggregation below is written for this check: a face with holes has vertices + 2 * holes - 2 triangles.
Usage: OPENSKP_PY=<dir that contains the openskp package> python3 -I make_skp_legacy_golden.py <samples dir> <out.json>"""
import sys,json,glob,os
sys.path.insert(0,os.environ['OPENSKP_PY'])
from openskp.legacy import full_parse_legacy
def run(path):
    r=full_parse_legacy(path)
    defs={k:v['builder'] for k,v in r['defs_dict'].items()}
    root=defs['ROOT']
    acc={'verts':0,'tris':0,'pts':[],'faces':0}
    def count(b,M,depth=0):
        if depth>40: return
        for fid,f in b.faces.items():
            if f['hidden']: continue
            nv=0;nl=0
            for lp in f['loops']:
                pts=[];prev=None
                for u in lp:
                    e=u[0]
                    if e not in b.edges: continue
                    a,c=b.edges[e]
                    v=a if u[1]==1 else c
                    if v==prev or v not in b.vertices: continue
                    pts.append(v);prev=v
                if len(pts)>1 and pts[0]==pts[-1]: pts.pop()
                if len(pts)>=3: nv+=len(pts);nl+=1
                if len(pts)>=3 and lp is f['loops'][0] or (nl==1 and len(pts)>=3 and not acc.get('x')):
                    pass
            if nl:
                acc['verts']+=nv;acc['tris']+=nv+2*(nl-1)-2;acc['faces']+=1
                # bounds from all loops' vertices of first loop with >=3 pts
                for lp in f['loops']:
                    for u in lp:
                        e=u[0]
                        if e in b.edges:
                            for v in b.edges[e]:
                                if v in b.vertices:
                                    x,y,z=b.vertices[v]
                                    acc['pts'].append((M[0]*x+M[1]*y+M[2]*z+M[9],M[3]*x+M[4]*y+M[5]*z+M[10],M[6]*x+M[7]*y+M[8]*z+M[11]))
                    break
        for inst in b.instances:
            if inst['hidden'] or inst['ref_idx'] not in defs: continue
            mm=inst['matrix']; s=mm[12] if mm[12] else 1.0
            m=[x*s for x in mm[:9]]+list(mm[9:12])
            C=[0]*12
            for r_ in range(3):
                for c in range(3): C[r_*3+c]=sum(M[r_*3+k]*m[k*3+c] for k in range(3))
                C[9+r_]=sum(M[r_*3+k]*m[9+k] for k in range(3))+M[9+r_]
            count(defs[inst['ref_idx']],C,depth+1)
    count(root,[1,0,0,0,1,0,0,0,1,0,0,0])
    p=acc['pts']
    bb=[min(q[i] for q in p) for i in range(3)]+[max(q[i] for q in p) for i in range(3)] if p else None
    return {'tris':acc['tris'],'verts':acc['verts'],'bounds':bb}
out={}
for f in sorted(glob.glob(sys.argv[1]+'/sketchup/*.skp')):
    d=open(f,'rb').read(); n=d[35]
    if d[36+2*n:39+2*n]==b'VFF': continue
    try: out[os.path.basename(f)]=run(f)
    except Exception as e: print('FAIL',f,e); continue
json.dump(out,open(sys.argv[2],'w'),indent=1)
for k,v in out.items(): print(k,v['tris'],v['verts'],[round(x,3) for x in v['bounds']] if v['bounds'] else None)
