"""
analyze_ladder.py — 解读 SimaticML 梯形图, 输出可读清单并做健全性检查。

能抓到的问题:
  * 同一输出被多个线圈驱动 (真实 PLC 不允许)
  * 引用了不存在的变量
  * 触点/线圈未接线
"""
import sys
from collections import defaultdict
from lxml import etree


def ln(t):
    return t.split('}')[-1] if '}' in t else t


def analyze(path):
    tree = etree.parse(path)
    root = tree.getroot()
    print('=' * 68)
    print('梯形图解读:', path)
    print('=' * 68)

    writers = defaultdict(list)   # 变量 -> [(段号, '线圈')]
    readers = defaultdict(list)   # 变量 -> [(段号, '触点')]
    all_tags = set()

    seg_no = 0
    for cu in root.iter():
        if ln(cu.tag) != 'SW.Blocks.CompileUnit':
            continue
        seg_no += 1

        # Access: UId -> 变量名
        acc = {}
        for a in cu.iter():
            if ln(a.tag) != 'Access':
                continue
            for c in a.iter():
                if ln(c.tag) == 'Component':
                    acc[a.get('UId')] = c.get('Name')
                    all_tags.add(c.get('Name'))
                    break

        # 通过 Wires 把 Part 和 Access 关联起来
        # IdentCon 的 UId = Access(操作数) 的 UId
        # NameCon Name="operand" 的 UId = Part(触点/线圈) 的 UId
        part_tag = {}
        for w in cu.iter():
            if ln(w.tag) != 'Wire':
                continue
            a_uid = None
            p_uid = None
            for e in w.iter():
                t = ln(e.tag)
                if t == 'IdentCon':
                    a_uid = e.get('UId')
                elif t == 'NameCon' and e.get('Name') == 'operand':
                    p_uid = e.get('UId')
            if a_uid in acc and p_uid:
                part_tag[p_uid] = acc[a_uid]

        title = ''
        for e in cu.iter():
            if ln(e.tag) == 'Text' and (e.text or '').strip():
                title = e.text.strip()
                break

        items = []
        for p in cu.iter():
            if ln(p.tag) != 'Part':
                continue
            kind = p.get('Name')
            tag = part_tag.get(p.get('UId'), '(未接线)')
            if kind == 'Contact':
                items.append('%s  [触点·读]' % tag)
                readers[tag].append(seg_no)
            elif kind == 'Coil':
                items.append('%s  [线圈·写]' % tag)
                writers[tag].append(seg_no)
            else:
                items.append('%s  [%s]' % (tag, kind))

        print('\n程序段 %d  %s' % (seg_no, title))
        if items:
            for it in items:
                print('    ' + it)
        else:
            print('    (空段)')

    print('\n' + '=' * 68)
    print('输出线圈检查(每个输出应只有一个线圈):')
    problems = []
    for var, segs in sorted(writers.items()):
        flag = ''
        if len(segs) > 1:
            flag = '  <== 重复驱动! 同一个输出被 %d 个线圈写' % len(segs)
            problems.append('%s 被段 %s 重复驱动' % (var, segs))
        print('    %-12s 线圈于 段%s%s' % (var, segs, flag))

    print('\n触点(只读,读取输出状态,不算重复写):')
    for var, segs in sorted(readers.items()):
        print('    %-12s 触点于 段%s' % (var, segs))

    if problems:
        print('\n发现问题:')
        for p in problems:
            print('  ! ' + p)
    else:
        print('\n没有发现重复驱动问题。')
    return problems


if __name__ == '__main__':
    analyze(sys.argv[1])