#!/usr/bin/env python3
"""
lad_logic_sim.py — 梯形图逻辑仿真器(不需要 PLCSIM, 在本机直接跑)。

为什么需要它: 博途跑真仿真要手动"下载到设备" + 手动启动 PLCSIM, 很麻烦。
而**纯布尔逻辑**的梯形图(电机启停、互锁、报警联锁这类)完全可以在本机验证逻辑。

做法: 解析真实导出的 SimaticML -> 重建电气节点 -> 从左母线传播求值
      线圈输出默认取上一周期值(这正是自保持/锁定的物理行为)。

明确局限(不会假装算对了):
  ✗ 不支持 定时器/计数器/边沿/比较运算/FB 调用
  ✓ 支持 触点(常开/常闭)、线圈、S/R 线圈、OR 并联、多网络

用法: python lad_logic_sim.py 导出的.xml
"""
import sys
import os
from collections import defaultdict
from lxml import etree


def ln(t):
    return t.split('}')[-1] if '}' in t else t


SUPPORTED = ('Contact', 'Coil', 'SCoil', 'RCoil', 'O')


class Net:
    def __init__(self):
        self.parts = {}     # uid -> {kind, tag, negated, value}
        self.edges = []     # (src_uid, dst_uid)  src.out -> dst.in
        self.rail = set()   # 由左母线直接供电的 uid


def parse(cu):
    net = Net()
    acc = {}
    for a in cu.iter():
        if ln(a.tag) == 'Access':
            for c in a.iter():
                if ln(c.tag) == 'Component':
                    acc[a.get('UId')] = c.get('Name')
                    break
    for p in cu.iter():
        if ln(p.tag) == 'Part':
            neg = any(ln(x.tag) == 'Negated' for x in p.iter())
            net.parts[p.get('UId')] = {'kind': p.get('Name'), 'negated': neg,
                                       'tag': None, 'value': False}
    # 精确定位每个 Wire(用 local-name 匹配, 避免命名空间与跨网络混入)
    wires = [e for e in cu.iter() if ln(e.tag) == 'Wire']
    for w in wires:
        has_rail = any(ln(x.tag) == 'Powerrail' for x in w.iter())
        a_uid = None
        cons = []   # (uid, pin)
        for e in w.iter():
            t = ln(e.tag)
            if t == 'IdentCon':
                a_uid = e.get('UId')
            elif t == 'NameCon':
                cons.append((e.get('UId'), e.get('Name')))
        outs = [u for u, p in cons if p == 'out']
        ins = [(u, p) for u, p in cons if p and p.startswith('in')]
        # 操作数连线: NameCon Name="operand" 指向 Part, IdentCon 指向 Access
        operand_part = None
        for u, p in cons:
            if p == 'operand':
                operand_part = u
        if a_uid and a_uid in acc:
            if operand_part in net.parts:
                net.parts[operand_part]['tag'] = acc[a_uid]
            continue
        for u, p in ins:
            if has_rail and not outs:
                net.rail.add(u)
            for s in outs:
                if s in net.parts and u in net.parts:
                    net.edges.append((s, u))
    return net


def collect_outputs(nets):
    """收集所有线圈驱动的变量名(这些是输出, 不是外部输入)。"""
    outs = set()
    for n in nets:
        for p in n.parts.values():
            if p['kind'] in ('Coil', 'SCoil', 'RCoil') and p['tag']:
                outs.add(p['tag'])
    return outs


def evaluate(net, values, prev, scanned=None, global_coils=None):
    """
    电气模型:
      触点导通 = 上游有电 AND 自身条件成立
        - 上游有电: 左母线直供, 或经由串联触点/OR 传来的电
        - 自身条件: NO = 输入为1; NC = 输入为0
        - 自保持触点读的是**上一扫描周期**的线圈输出
      OR 部件输出 = 任一输入有电(或运算)
      线圈: 普通线圈取本周期; S 置位后保持; R 复位后保持

    scanned: 本次扫描中**前面网络**已经算出的输出(同一扫描周期按段序执行)。
    global_coils: 全部网络里被线圈驱动的变量名(跨网络收集, 因为各网络的 Part
                  uid 是独立编号的, 不能只看本网络判断某变量是不是输出)。
    """
    if scanned is None:
        scanned = {}
    if global_coils is None:
        global_coils = {p['tag'] for p in net.parts.values()
                        if p['kind'] in ('Coil', 'SCoil', 'RCoil') and p['tag']}

    # 1) 每个触点先算"自身条件"
    for uid, p in net.parts.items():
        if p['kind'] != 'Contact':
            continue
        if p['tag'] in global_coils:
            # 触点读的是被驱动它的那个线圈(本扫描周期前面网络的结果, 或上一周期的)
            v = bool(scanned.get(p['tag'], prev.get(p['tag'], False)))
        else:
            v = bool(values.get(p['tag'], False))
        p['cond'] = (not v) if p['negated'] else v
        p['value'] = p['cond']

    incoming = defaultdict(list)
    for s, d in net.edges:
        incoming[d].append(s)

    # 2) 迭代传播(级联串联 + OR 汇合)
    for _ in range(12):
        for uid, p in net.parts.items():
            if p['kind'] == 'Contact':
                if uid in net.rail:
                    p['power'] = True
                else:
                    p['power'] = any(net.parts[u]['value'] for u in incoming.get(uid, [])
                                     if u in net.parts)
                p['value'] = p['power'] and p['cond']
            elif p['kind'] == 'O':
                p['value'] = any(net.parts[u]['value'] for u in incoming.get(uid, [])
                                 if u in net.parts)

    # 3) 线圈输出
    out = {}
    for uid, p in net.parts.items():
        if p['kind'] in ('Coil', 'SCoil', 'RCoil'):
            on = any(net.parts[u]['value'] for u in incoming.get(uid, []) if u in net.parts)
            if p['kind'] == 'Coil':
                out[p['tag']] = on
            elif p['kind'] == 'SCoil':
                out[p['tag']] = True if on else prev.get(p['tag'], False)
            else:
                out[p['tag']] = False if on else prev.get(p['tag'], False)
    return out


def main():
    tree = etree.parse(sys.argv[1])
    nets, tags = [], set()
    for cu in tree.getroot().iter():
        if ln(cu.tag) != 'SW.Blocks.CompileUnit':
            continue
        n = parse(cu)
        if n.parts:
            nets.append(n)
            for p in n.parts.values():
                if p['tag']:
                    tags.add(p['tag'])

    unsupported = sorted({p['kind'] for n in nets for p in n.parts.values()
                          if p['kind'] not in SUPPORTED})
    if unsupported:
        print('!! 暂不支持的元件 %s —— 仿真结果不可信' % unsupported)

    print('解析到 %d 个程序段, 涉及变量: %s\n' % (len(nets), ', '.join(sorted(tags))))

    def mk(d):
        return {t: bool(d.get(0)) for t in tags} if False else {t: bool(d.get(t, 0)) for t in tags}

    # 电机启停回路的标准测试序列。
    # 注意物理语义: 停止按钮 / 热继电器是**常闭接法** —— 正常时输入=1,
    # 按下停止 / 热保护动作时输入=0。所以"按下停止"是 停止按钮=0。
    seq = [
        ('上电(停止&热保护正常)', {'停止按钮': 1, '热保护': 1}),
        ('按启动',                 {'停止按钮': 1, '热保护': 1, '启动按钮': 1}),
        ('松开启动(测自保持)',     {'停止按钮': 1, '热保护': 1}),
        ('按停止(停止->0)',        {'停止按钮': 0, '热保护': 1}),
        ('松开停止',               {'停止按钮': 1, '热保护': 1}),
        ('热保护动作(->0)',        {'停止按钮': 1, '热保护': 0}),
        ('热保护恢复(测锁定)',     {'停止按钮': 1, '热保护': 1}),
    ]

    # 期望的电机启停行为(接触器 / 运行指示)
    EXPECT = [0, 1, 1, 0, 0, 0, 0]

    print('%-24s %-9s %-11s' % ('步骤', '接触器', '运行指示'))
    print('-' * 50)
    prev = {}
    got, lamps = [], []
    global_coils = {p['tag'] for n in nets for p in n.parts.values()
                    if p['kind'] in ('Coil', 'SCoil', 'RCoil') and p['tag']}
    for label, d in seq:
        ins = mk(d)
        scanned = {}
        for n in nets:
            scanned.update(evaluate(n, ins, prev, scanned, global_coils))
        c = 1 if scanned.get('接触器') else 0
        r = 1 if scanned.get('运行指示') else 0
        got.append(c)
        lamps.append(r)
        print('%-24s %-9d %-11d' % (label, c, r))
        prev.update(scanned)

    ok_motor = got == EXPECT
    ok_lamp = lamps == EXPECT
    print()
    print('接触器    : %s  期望 %s  %s' % (got, EXPECT, '✓' if ok_motor else '✗'))
    print('运行指示  : %s  期望 %s  %s' % (lamps, EXPECT, '✓' if ok_lamp else '✗'))
    if ok_motor and ok_lamp:
        print('\n结论: 回路逻辑完全正确(自保持 / 停止优先 / 热保护锁定 全部符合预期)')
    else:
        print('\n结论: 回路逻辑与预期不符 —— 请检查触点类型(常开/常闭)是否与按钮接法匹配')
    return 0 if (ok_motor and ok_lamp) else 1


if __name__ == '__main__':
    sys.exit(main())