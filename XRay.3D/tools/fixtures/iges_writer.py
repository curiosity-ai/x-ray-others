import math
def fmt(v):
    if isinstance(v, int): return str(v)
    return repr(float(v))
def write_iges(path, entities, units='MM'):
    """entities: list of dict(type, form=0, params=[...], status='00000000', level=0, color=0)"""
    start = ['x-ray test fixture'.ljust(72) + 'S      1']
    g = ['1H,,1H;,4HTEST,8Htest.igs,8Hxray-3d1,8Hxray-3d2,32,38,6,308,15,4HTEST,1.0,2,2H%s,1,0.01,15H20260101.000000,1E-06,1000.0,6Htester,6Hxray3d,11,0,;' % units]
    gl = []
    text = g[0]
    while text:
        gl.append(text[:72]); text = text[72:]
    gsec = [l.ljust(72) + 'G%7d' % (i + 1) for i, l in enumerate(gl)]
    dlines = []; plines = []
    pseq = 0
    for k, e in enumerate(entities):
        de = 2 * k + 1
        pdata = [str(e['type'])] + [fmt(p) for p in e['params']]
        body = ','.join(pdata) + ';'
        chunks = []
        cur = ''
        # split into <= 64 col lines at comma boundaries
        parts = body.replace(';', ',;').split(',')
        cur = ''
        out = []
        for part in parts:
            tok = part + (',' if part != ';' else '')
            if part == '': continue
            if len(cur) + len(tok) > 64:
                out.append(cur); cur = ''
            cur += tok
        if cur: out.append(cur)
        # last token must end with ';' not ',;'
        out[-1] = out[-1].replace(',;', ';')
        first = pseq + 1
        for l in out:
            pseq += 1
            plines.append(l.ljust(64) + ' ' + ('%7d' % de) + 'P' + '%7d' % pseq)
        n = len(out)
        d1 = '%8d%8d%8d%8d%8d%8d%8d%8d%s' % (e['type'], first, 0, e.get('level', 0), 0, e.get('color', 0), 0, 0, e.get('status', '00000000'))
        d2 = '%8d%8d%8d%8d%8d%8s%8s%8s%8d' % (e['type'], 0, e.get('color', 0), n, e.get('form', 0), '', '', '', 0)
        dlines.append(d1[:72].ljust(72) + 'D%7d' % (2 * k + 1))
        dlines.append(d2[:72].ljust(72) + 'D%7d' % (2 * k + 2))
    t = 'S%7dG%7dD%7dP%7d' % (len(start), len(gsec), len(dlines), len(plines))
    lines = start + gsec + dlines + plines + [t.ljust(72) + 'T      1']
    open(path, 'w').write('\n'.join(lines) + '\n')
