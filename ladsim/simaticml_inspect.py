#!/usr/bin/env python3
"""
simaticml_inspect.py — SimaticML (TIA Portal engineering XML) 结构剖析工具.

用途（阶段 6：LAD 生成器的地基）:
  在"绝不凭空编 XML"的前提下, 先用一个通用的结构分析器把**真实导出的** SimaticML
  文件拆开看清楚: 有哪些块 / 每个块有哪几个 Network / Network 里有哪��� Part /
  每个 Part 的类型和连接 / Wire 是怎么把 Part 连起来的 / Operand 怎么引用 %I0.0。

  本工具只做"读"和"报", 不生成 XML。它不知道 LAD 的语义, 只忠实呈现
  文档里真实存在的元素名、属性和层次关系。

用法:
  python simaticml_inspect.py <file.xml>              # 概要: 块 / Network 清单
  python simaticml_inspect.py <file.xml> --tree       # 完整元素树(带属性)
  python simaticml_inspect.py <file.xml> --networks   # 每个 Network 逐个 Part/Wire 明细
  python simaticml_inspect.py <file.xml> --wires      # 汇总所有 Wire 连接关系
  python simaticml_inspect.py <file.xml> --tag <id>   # 查某个 LocalId / UId 的所有出现
"""

import argparse
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict


def lname(tag):
    """去掉命名空间, 只留 local-name (SimaticML 的命名空间随版本变化, 所以统一处理)."""
    return tag.split('}', 1)[-1] if '}' in tag else tag


def walk(el, depth=0):
    """深度优先遍历, 产出 (element, depth)."""
    yield el, depth
    for child in el:
        yield from walk(child, depth + 1)


def attrs_of(el):
    return {k.split('}', 1)[-1]: v for k, v in el.attrib.items()}


def load(path):
    try:
        tree = ET.parse(path)
    except ET.ParseError as e:
        print(f"[X] XML 解析失败: {e}", file=sys.stderr)
        sys.exit(2)
    return tree.getroot()


def find_all(root, name):
    return [el for el, _ in walk(root) if lname(el.tag) == name]


def summarize(root):
    """概要: 文档里都有哪些块, 每个块什么语言/块号, 每个块几个 Network."""
    print("=" * 70)
    print("文档根元素:", lname(root.tag))
    root_attrs = attrs_of(root)
    for k, v in root_attrs.items():
        print(f"  @{k} = {v}")

    blocks = find_all(root, "Block")
    print(f"\n共找到 {len(blocks)} 个 <Block> 元素")

    # 网络(Network)在 SimaticML 里可能叫 Network / Networks / 等, 泛化查找
    net_names = set()
    for el, _ in walk(root):
        if lname(el.tag).lower().startswith("network"):
            net_names.add(lname(el.tag))
    print("文档中出现的 Network 类元素名:", sorted(net_names) if net_names else "(无)")

    for b in blocks:
        a = attrs_of(b)
        print("\n" + "-" * 60)
        print("Block 属性:")
        for k, v in a.items():
            print(f"    @{k} = {v}")
        # 块类型/语言通常在子元素里
        for el, d in walk(b):
            if d > 2:
                break
            n = lname(el.tag)
            if n in ("Name", "Number", "ProgrammingLanguage", "BlockType",
                     "Interface", "Memory", "LocalInterfaces"):
                print(f"    <{n}> = {''.join(el.itertext()).strip()[:80]}")
        nets = [el for el, _ in walk(b) if lname(el.tag).lower().startswith("network")]
        print(f"    => 该块下 Network 类元素 {len(nets)} 个")


def print_tree(root, max_depth=99):
    def rec(el, d=0):
        if d > max_depth:
            return
        a = attrs_of(el)
        astr = " ".join(f'@{k}="{v}"' for k, v in a.items())
        txt = (el.text or "").strip()
        suffix = f"  text={txt!r}" if txt else ""
        print("  " * d + f"<{lname(el.tag)}{(' ' + astr) if astr else ''}>{suffix}")
        for c in el:
            rec(c, d + 1)
    print("=" * 70)
    print("完整元素树 (local-name):")
    rec(root)


def part_like(el):
    """判断一个元素是否像'零件'(触点/线圈/指令等), 用于 Network 明细."""
    n = lname(el.tag)
    keys = ("Part", "Contact", "Coil", "Box", "Call", "Operand", "Access",
            "Instruction", "Function", "Move", "Powerrail", "Rail", "Comment")
    return any(k.lower() in n.lower() for k in keys)


def networks_detail(root):
    """逐个 Network 打印 Part / Wire 明细."""
    nets = [el for el, _ in walk(root)
            if lname(el.tag).lower().startswith("network")]
    print(f"共 {len(nets)} 个 Network")
    for idx, net in enumerate(nets, 1):
        a = attrs_of(net)
        print("\n" + "=" * 66)
        print(f"[Network #{idx}] <{lname(net.tag)}> 属性: {a}")
        parts = [el for el, _ in walk(net) if part_like(el)]
        print(f"  该 Network 下零件类元素 {len(parts)} 个:")
        for p in parts:
            pa = attrs_of(p)
            txt = "".join(p.itertext()).strip()
            short = " ".join(f"@{k}={v}" for k, v in pa.items())
            print(f"    <{lname(p.tag)}> {short}" + (f"  text={txt!r}" if txt else ""))


def wires_summary(root):
    """汇总所有 Wire 及其连接端点 —— 这是 LAD 连线的核心."""
    wires = find_all(root, "Wire")
    print(f"共 {len(wires)} 个 <Wire>")
    conn_index = defaultdict(list)
    for w in wires:
        wa = attrs_of(w)
        # 每个 Wire 下通常有若干 ConnectionPoint / 各含 Ref 属性指向 LocalId
        points = []
        for el, _ in walk(w):
            if lname(el.tag) in ("ConnectionPoint", "EndPoint", "Connect"):
                pa = attrs_of(el)
                ref = pa.get("Ref") or pa.get("UId") or pa.get("UID") or pa.get("ref")
                if ref:
                    points.append(ref)
        line = ", ".join(points) if points else "(无连接点)"
        print(f"  Wire {wa.get('UId', '?')} / {wa.get('Name','')} : {line}")
        for i, p in enumerate(points):
            conn_index[p].append((wa.get("UId", "?"), i))
    print("\n--- 反查: 某个 LocalId 出现在哪些 Wire ---")
    for p, ws in conn_index.items():
        print(f"  {p} -> {ws}")


def tag_lookup(root, tag):
    print(f"查找所有出现 {tag} 的位置:")
    found = 0
    for el, d in walk(root):
        a = attrs_of(el)
        if tag in a.values() or any(tag == v for v in a.values()):
            print(f"  <{lname(el.tag)}> @{a}  (深度 {d})")
            found += 1
    # 也找同名元素
    same = [el for el, _ in walk(root) if lname(el.tag) == tag]
    for el in same:
        print(f"  元素本身 <{lname(el.tag)}> @{attrs_of(el)}")
        found += 1
    if not found:
        print("  (未找到)")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("file")
    ap.add_argument("--tree", action="store_true", help="完整元素树")
    ap.add_argument("--networks", action="store_true", help="Network 零件明细")
    ap.add_argument("--wires", action="store_true", help="Wire 连接汇总")
    ap.add_argument("--tag", help="查某 LocalId/UId/Name 的出现")
    ap.add_argument("--depth", type=int, default=99, help="--tree 深度限制")
    args = ap.parse_args()

    root = load(args.file)
    did = False
    if args.tree:
        print_tree(root, args.depth); did = True
    if args.networks:
        networks_detail(root); did = True
    if args.wires:
        wires_summary(root); did = True
    if args.tag:
        tag_lookup(root, args.tag); did = True
    if not did:
        summarize(root)


if __name__ == "__main__":
    main()