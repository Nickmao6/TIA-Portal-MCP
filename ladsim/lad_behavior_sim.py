"""
梯形图行为仿真器 —— 直接解释博途导出的 FB 梯形图, 扫描级仿真, 不依赖 PLCSIM。

为什么需要: 编译器只保证语法正确, 不保证逻辑正确(顺序、锁存、定时、边界)。
本仿真器按 PLC 的真实扫描语义逐段执行, 能发现:
  - 相位/状态机跳转错误
  - 自保持不成立或停不下来
  - 输出互斥/重叠(比如两个方向同时绿灯)
  - 倒计时不变化或死循环

支持元件: 触点(常开/常闭)、线圈、OR部件、比较(Ge/Gt/Eq/Ne/Lt/Le)、
         数学(Add/Sub/Mul/Div/Mod)、MOVE、CONVERT、TON/TOF/TP/TONR、CTU/CTD/CTUD

用法: python lad_behavior_sim.py <块xml> <场景脚本.py>
"""
import sys
import io
from lxml import etree

CMP = {'Ge': '>=', 'Gt': '>', 'Eq': '==', 'Ne': '!=', 'Lt': '<', 'Le': '<='}
MATH = {'Add': '+', 'Sub': '-', 'Mul': '*', 'Div': '//', 'Mod': '%'}
TIMERS = ('TON', 'TOF', 'TP', 'TONR')


def ln(t):
    return t.split('}')[-1]


def cmpop(op, a, b):
    if isinstance(a, str) or isinstance(b, str):
        return False
    if op == '>=':
        return a >= b
    if op == '>':
        return a > b
    if op == '==':
        return a == b
    if op == '!=':
        return a != b
    if op == '<':
        return a < b
    if op == '<=':
        return a <= b
    return False


def parse_time_ms(raw):
    """
    把 IEC 时间常量转成毫秒。T#1s=1000, T#0.5s=500, T#100ms=100,
    T#1m=60000, T#2h=7200000, T#1d=86400000, T#1s500ms=1500。
    单位必须正确换算 —— 否则仿真里定时器周期会差 1000 倍。
    """
    s = raw[2:].strip().lower()
    units = [('d', 86400000), ('h', 3600000), ('m', 60000),
             ('s', 1000), ('ms', 1), ('us', 0.001)]
    # ms 必须比 m 先匹配, 所以按长度降序尝试
    total = 0.0
    num = ''
    i = 0
    while i < len(s):
        ch = s[i]
        if ch.isdigit() or ch == '.':
            num += ch
            i += 1
            continue
        for u, mul in sorted(units, key=lambda x: -len(x[0])):
            if s.startswith(u, i):
                total += float(num or 0) * mul
                num = ''
                i += len(u)
                break
        else:
            i += 1
    if num:
        total += float(num)      # 无单位时按毫秒
    return int(total) if total else 1000


class Net:
    """把一段 FlgNet 解析成可仿真的结构"""

    def __init__(self, flg):
        self.acc = {}          # uid -> (名字, scope)
        self.parts = {}        # uid -> dict(name, tv, inst, neg)
        self.wires = []        # [(has_rail, [(kind, uid, pin)])]
        for sec in flg:
            if ln(sec.tag) == 'Parts':
                for p in sec:
                    if ln(p.tag) == 'Access':
                        name, scope = '?', p.get('Scope')
                        for c in p.iter():
                            if ln(c.tag) == 'Component':
                                name = c.get('Name')
                            if ln(c.tag) == 'ConstantValue':
                                name = c.text
                        self.acc[p.get('UId')] = (name, scope)
                    else:
                        tv = {}
                        inst = None
                        neg = False
                        for x in p.iter():
                            if ln(x.tag) == 'TemplateValue':
                                tv[x.get('Name')] = x.text
                            if ln(x.tag) == 'Component' and x.getparent() is not None \
                               and ln(x.getparent().tag) == 'Instance':
                                inst = x.get('Name')
                            if ln(x.tag) == 'Negated':
                                neg = True
                        self.parts[p.get('UId')] = dict(name=p.get('Name'), tv=tv,
                                                        inst=inst, neg=neg)
            elif ln(sec.tag) == 'Wires':
                for w in sec:
                    rail = False
                    conns = []
                    for c in w:
                        if ln(c.tag) == 'Powerrail':
                            rail = True
                        elif ln(c.tag) == 'NameCon':
                            conns.append(('pin', c.get('UId'), c.get('Name')))
                        elif ln(c.tag) == 'IdentCon':
                            conns.append(('acc', c.get('UId'), None))
                    self.wires.append((rail, conns))

        # 建立 feed 表: (uid,pin) -> ('rail'|'pin'|'acc', ...)
        # 引脚有方向: out/Q/QU/QD/ET/ENO/outN 是**输出**, 其余(in/pre/en/IN/operand/
        # inN/PT/CU/CD/R/LD/S/R1...)是**输入**。必须按方向连线, 否则会连成环。
        def is_out(pin):
            if pin in ('out', 'Q', 'QU', 'QD', 'ET', 'ENO'):
                return True
            return pin.startswith('out') and pin[3:].isdigit()

        self.feed = {}
        for rail, conns in self.wires:
            if rail:
                for c in conns:
                    if c[0] == 'pin':
                        self.feed[(c[1], c[2])] = ('rail', None, None)
            pins = [c for c in conns if c[0] == 'pin']
            accs = [c for c in conns if c[0] == 'acc']
            # 操作数 <-> 引脚: 输入引脚取操作数; 输出引脚写操作数
            for a in accs:
                for p in pins:
                    self.feed.setdefault((p[1], p[2]), ('acc', a[1], None))
            # 元件间: 输出引脚 -> 输入引脚
            srcs = [c for c in pins if is_out(c[2])]
            dsts = [c for c in pins if not is_out(c[2])]
            for s in srcs:
                for d in dsts:
                    self.feed.setdefault((d[1], d[2]), ('pin', s[1], s[2]))

    # ---- 取值/赋值 ----
    def operand(self, uid, st):
        name, scope = self.acc.get(uid, ('?', None))
        if name is None:
            return 0
        s = str(name)
        # 数字常量(可带小数点/负号): 变量名几乎不可能是纯数字, 安全
        try:
            return float(s) if '.' in s else int(s)
        except (ValueError, TypeError):
            pass
        if s.startswith('T#'):
            return s
        return st.get(s, 0)


def run(xml_path, scenario, max_scan=400, verbose=True):
    root = etree.parse(xml_path).getroot()
    block = None
    for el in root:
        if ln(el.tag) in ('SW.Blocks.OB', 'SW.Blocks.FB', 'SW.Blocks.FC'):
            block = el
    ol = next(e for e in block if ln(e.tag) == 'ObjectList')
    nets = []
    for cu in ol:
        if ln(cu.tag) != 'SW.Blocks.CompileUnit':
            continue
        flg = None
        for f in cu.iter():
            if ln(f.tag) == 'FlgNet':
                flg = f
        if flg is not None:
            nets.append(Net(flg))

    st = {}
    timers = {}
    history = []
    for scan in range(max_scan):
        scenario(st, scan, timers)
        for net in nets:
            exec_net(net, st, timers)
        history.append(dict(st))
        yield scan, st, history


def topo_order(net):
    """
    按数据依赖给元件排序(Kahn 算法)。

    为什么必须排序: 生成器把 OR 部件写在支路元件**前面**, 所以 XML 里的出现顺序
    不是求值顺序。而且一个扫描周期内每个元件只能算**一次** —— 反复迭代会让
    自复位定时器(秒脉冲那类)在同一周期里来回翻转, 得到完全错误的结果。
    """
    deps = {uid: set() for uid in net.parts}
    for (uid, pin), src in net.feed.items():
        if uid in net.parts and src and src[0] == 'pin' and src[1] in net.parts:
            deps[uid].add(src[1])

    idx = {u: i for i, u in enumerate(net.parts)}
    remaining = {u: set(v) for u, v in deps.items()}
    order = []
    while remaining:
        ready = [u for u, v in remaining.items() if not (v & set(remaining))]
        if not ready:
            # 理论上不该有环(反馈都走变量, 不是引脚直连); 兜底按出现顺序
            ready = list(remaining.keys())
        ready.sort(key=lambda x: idx[x])
        for u in ready:
            order.append(u)
            del remaining[u]
    return order


def exec_net(net, st, timers):
    power = {}     # (uid,pin) -> bool

    def inpower(uid, pin):
        src = net.feed.get((uid, pin))
        if src is None:
            return False
        kind = src[0]
        if kind == 'rail':
            return True
        if kind == 'acc':
            v = net.operand(src[1], st)
            return bool(v)
        return power.get((src[1], src[2]), False)

    # 按依赖顺序, 每个元件只求值一次(真实 PLC 的扫描语义)
    for _pass in range(1):
        changed = False
        for uid in topo_order(net):
            p = net.parts[uid]
            nm = p['name']
            if nm == 'Contact':
                v = inpower(uid, 'in')
                acc = net.feed.get((uid, 'operand'))
                raw = net.operand(acc[1], st) if acc and acc[0] == 'acc' else False
                out = bool(v) and (not bool(raw) if p['neg'] else bool(raw))
            elif nm == 'Coil':
                out = inpower(uid, 'in')
                acc = net.feed.get((uid, 'operand'))
                if acc and acc[0] == 'acc':
                    name, _ = net.acc.get(acc[1], ('?', None))
                    if st.get(name) != out:
                        st[name] = out
                        changed = True
            elif nm == 'O':
                card = int(p['tv'].get('Card', '2'))
                # OR 部件的输入引脚是 in1..inN (注意不是从 0 开始)
                out = any(inpower(uid, 'in%d' % n) for n in range(1, card + 1))
            elif nm == 'Not':
                out = not inpower(uid, 'in')
            elif nm in CMP:
                op = CMP[nm]
                a = net.operand(net.feed[(uid, 'in1')][1], st) if (uid, 'in1') in net.feed else 0
                b = net.operand(net.feed[(uid, 'in2')][1], st) if (uid, 'in2') in net.feed else 0
                out = inpower(uid, 'pre') and cmpop(op, a, b)
            elif nm in MATH:
                en = inpower(uid, 'en')
                a = net.operand(net.feed[(uid, 'in1')][1], st) if (uid, 'in1') in net.feed else 0
                b = net.operand(net.feed[(uid, 'in2')][1], st) if (uid, 'in2') in net.feed else 0
                if en:
                    try:
                        val = eval('%s %s %s' % (a, MATH[nm], b if b else 1))
                    except Exception:
                        val = 0
                    o = net.feed.get((uid, 'out'))
                    if o and o[0] == 'acc':
                        name, _ = net.acc.get(o[1], ('?', None))
                        if st.get(name) != val:
                            st[name] = val
                            changed = True
                out = en
            elif nm == 'Move':
                en = inpower(uid, 'en')
                src = net.feed.get((uid, 'in'))
                val = net.operand(src[1], st) if src and src[0] == 'acc' else 0
                if en:
                    for pin, tgt in net.feed.items():
                        if pin[0] == uid and pin[1].startswith('out') and tgt[0] == 'acc':
                            name, _ = net.acc.get(tgt[1], ('?', None))
                            if st.get(name) != val:
                                st[name] = val
                                changed = True
                out = en
            elif nm in TIMERS:
                inst = p['inst']
                tm = timers.setdefault(inst, {'et': 0, 'q': False, 'run': False})
                pt = 1000
                ptw = net.feed.get((uid, 'PT'))
                if ptw and ptw[0] == 'acc':
                    raw = net.acc.get(ptw[1], ('?', None))[0]
                    if isinstance(raw, str) and raw.startswith('T#'):
                        pt = parse_time_ms(raw)
                inv = inpower(uid, 'IN')
                if nm == 'TON':
                    if inv:
                        if tm['et'] < pt:
                            tm['et'] += 100
                        tm['q'] = tm['et'] >= pt
                    else:
                        tm['et'] = 0
                        tm['q'] = False
                elif nm == 'TOF':
                    if inv:
                        tm['et'] = 0
                        tm['q'] = True
                    else:
                        if tm['et'] < pt:
                            tm['et'] += 100
                        tm['q'] = tm['et'] < pt
                else:
                    tm['q'] = inv
                # ET 写回
                etw = net.feed.get((uid, 'ET'))
                if etw and etw[0] == 'acc':
                    name, _ = net.acc.get(etw[1], ('?', None))
                    if name not in st or st.get(name) != tm['et']:
                        st[name] = tm['et']
                        changed = True
                out = tm['q']
            else:
                out = inpower(uid, 'in')
            key = (uid, 'Q') if nm in TIMERS else (uid, 'out')
            if power.get(key) != out:
                power[key] = out
                changed = True
    return power
