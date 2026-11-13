from lxml import etree
def local(el): return etree.QName(el).localname

def parse(path):
    t = etree.parse(path); root = t.getroot()
    out = []
    cus = [e for e in root.iter() if local(e).endswith('CompileUnit')]
    for idx, cu in enumerate(cus, 1):
        ti = ''
        for e in cu.iter():
            if local(e) == 'MultilingualText' and e.get('CompositionName') == 'Title':
                for x in e.iter():
                    if local(x) == 'Text':
                        ti = ''.join(x.itertext()).strip(); break
        flg = None
        for e in cu.iter():
            if local(e) == 'FlgNet': flg = e; break
        n = {'idx': idx, 'title': ti, 'elems': {}, 'wires': []}
        if flg is None: out.append(n); continue
        for e in flg.iter():
            ln = local(e); uid = e.get('UId')
            if ln == 'Access' and uid:
                comps = [c.get('Name') for c in e.iter() if local(c) == 'Component' and c.get('Name')]
                addr = ''
                for a in e.iter():
                    if local(a) == 'Address' and a.get('Name'): addr = a.get('Name')
                n['elems'][uid] = ('.'.join(comps) if comps else addr) or '?'
            elif ln == 'Part' and uid: n['elems'][uid] = '%s[%s]' % (e.get('Name'), uid)
            elif ln == 'Call' and uid:
                ci = [c for c in e.iter() if local(c) == 'CallInfo']
                n['elems'][uid] = 'CALL %s[%s]' % (ci[0].get('Name') if ci else '?', uid)
        for w in flg.iter():
            if local(w) != 'Wire': continue
            ends = []
            for c in w:
                ln = local(c)
                if ln == 'Powerrail': ends.append('电源')
                elif ln in ('IdentCon', 'NameCon'):
                    u = c.get('UId'); pin = c.get('Name') or ''
                    lab = n['elems'].get(u, '?' + str(u))
                    ends.append('%s.%s' % (lab, pin) if pin else lab)
            if len(ends) >= 2: n['wires'].append(' <-> '.join(ends))
        out.append(n)
    return out

def show(path, name, only=None):
    nets = parse(path)
    print('===== %s =====' % name)
    for n in nets:
        if only and n['idx'] not in only: continue
        print('\n[段%d] %s' % (n['idx'], n['title'] or '(无标题)'))
        for w in n['wires']: print('   ', w)
    print()

show(r'out\wash_Main.xml', 'Main', only={3,4,17,18})
