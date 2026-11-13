"""
把生成的梯形图(FlgNet)翻译成可读的逻辑表达式, 用于人工核对。

为什么需要: 编译器只保证"语法正确", 不保证"逻辑符合需求"。把网络打印成
  条件 => 输出
形式后, 可以逐段对照工艺要求检查。

用法: python dump_ladder.py <块导出xml>
"""
import sys
import io
from lxml import etree

NS = '{http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5}'


def ln(t):
    return t.split('}')[-1]


CMP = {'Ge': '>=', 'Gt': '>', 'Eq': '==', 'Ne': '<>', 'Lt': '<', 'Le': '<='}
MATH = {'Add': '+', 'Sub': '-', 'Mul': '*', 'Div': '/', 'Mod': 'MOD'}
TIMER = ('TON', 'TOF', 'TP', 'TONR')
COUNTER = ('CTU', 'CTD', 'CTUD')


def load(xml_path):
    t = etree.parse(xml_path)
    root = t.getroot()
    block = None
    for el in root:
        if ln(el.tag) in ('SW.Blocks.OB', 'SW.Blocks.FB', 'SW.Blocks.FC'):
            block = el
    return block


def networks(block):
    ol = next(e for e in block if ln(e.tag) == 'ObjectList')
    out = []
    for cu in ol:
        if ln(cu.tag) != 'SW.Blocks.CompileUnit':
            continue
        title = ''
        for mt in cu.iter():
            if ln(mt.tag) == 'MultilingualText' and mt.get('CompositionName') == 'Title':
                for tx in mt.iter():
                    if ln(tx.tag) == 'Text' and (tx.text or '').strip():
                        title = tx.text.strip()
        flg = None
        for f in cu.iter():
            if ln(f.tag) == 'FlgNet':
                flg = f
        out.append((title, flg))
    return out


def parse_net(flg):
    """返回 (access, parts, wires) —— access: uid->名字; parts: uid->(类型, 额外信息)"""
    acc, parts, wires = {}, {}, []
    body = flg
    for sec in body:
        if ln(sec.tag) == 'Parts':
            for p in sec:
                if ln(p.tag) == 'Access':
                    name = '?'
                    for c in p.iter():
                        if ln(c.tag) == 'Component':
                            name = c.get('Name')
                    if p.get('Scope') == 'LiteralConstant':
                        for c in p.iter():
                            if ln(c.tag) == 'ConstantValue':
                                name = c.text
                    acc[p.get('UId')] = name
                else:
                    nm = p.get('Name')
                    extra = {}
                    for tv in p.iter():
                        if ln(tv.tag) == 'TemplateValue':
                            extra[tv.get('Name')] = tv.text
                    for inst in p.iter():
                        if ln(inst.tag) == 'Instance':
                            for c in inst.iter():
                                if ln(c.tag) == 'Component':
                                    extra['_inst'] = c.get('Name')
                    for neg in p.iter():
                        if ln(neg.tag) == 'Negated':
                            extra['_neg'] = neg.get('Name')
                    parts[p.get('UId')] = (nm, extra)
        elif ln(sec.tag) == 'Wires':
            for w in sec:
                conns = []
                has_rail = False
                for c in w:
                    if ln(c.tag) == 'Powerrail':
                        has_rail = True
                    elif ln(c.tag) == 'NameCon':
                        conns.append(('pin', c.get('UId'), c.get('Name')))
                    elif ln(c.tag) == 'IdentCon':
                        conns.append(('acc', c.get('UId'), None))
                wires.append((w.get('UId'), has_rail, conns))
    return acc, parts, wires


def ncon_name(uid, pin, parts):
    """把 (uid, pin) 显示成可读名字"""
    if uid in parts:
        nm, extra = parts[uid]
        if nm in CMP:
            return '[' + CMP[nm] + ']'
        if nm in ('Contact',):
            return '触点' + ('(常闭)' if extra.get('_neg') else '')
        if nm == 'Coil':
            return '线圈'
        if nm in TIMER or nm in COUNTER:
            return nm + '(' + extra.get('_inst', '?') + ')'
        if nm == 'Move':
            return 'MOVE'
        if nm in MATH:
            return '[' + MATH[nm] + ']'
        if nm == 'O':
            return 'OR(' + extra.get('Card', '?') + ')'
        if nm == 'Not':
            return 'NOT'
        if nm == 'Convert':
            return 'CONV'
        return nm
    return uid


def describe(flg):
    acc, parts, wires = parse_net(flg)

    # 每个元件的输入连接(谁给它电) 和操作数
    feed = {}       # (uid,pin)  -> (src_uid, src_pin) 或者 ('acc', 变量名)
    for wid, has_rail, conns in wires:
        for i in range(len(conns) - 1):
            a, b = conns[i], conns[i + 1]
            # 任意方向都记
            if a[0] == 'pin' and b[0] == 'pin':
                feed.setdefault((b[1], b[2]), (a[1], a[2]))
            elif a[0] == 'acc' and b[0] == 'pin':
                feed.setdefault((b[1], b[2]), ('acc', acc.get(a[1], '?')))
            elif a[0] == 'pin' and b[0] == 'acc':
                feed.setdefault((b[1], b[2]), ('acc', acc.get(b[1], '?')))
        if has_rail:
            for c in conns:
                if c[0] == 'pin':
                    feed.setdefault((c[1], c[2]), ('rail', None))

    lines = []
    for uid, (nm, extra) in parts.items():
        # 输出接到了哪
        outs = []
        for (u, pin), src in feed.items():
            pass
        # 该元件的操作数
        ops = []
        for (u, pin), src in feed.items():
            if u == uid and isinstance(src, tuple) and src[0] == 'acc':
                ops.append('%s=%s' % (pin, src[1]))
        if not ops:
            continue
        lines.append('    %s %s' % (ncon_name(uid, None, parts), ' '.join(sorted(ops))))
    return lines


def main(xml_path):
    block = load(xml_path)
    print('块: %s' % block.findtext('AttributeList/Name'))
    print()
    for i, (title, flg) in enumerate(networks(block), 1):
        if flg is None:
            print('段%02d %s  (空段)' % (i, title))
            continue
        print('段%02d %s' % (i, title))
        for line in describe(flg):
            print(line)
        print()


if __name__ == '__main__':
    main(sys.argv[1])