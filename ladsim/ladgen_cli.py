#!/usr/bin/env python3
"""
ladgen_cli.py — 通用 LAD 梯形图生成器。

用一份 JSON 规格描述梯形图, 生成可直接导入 TIA Portal 的 SimaticML XML。
所有格式细节的依据见 LAD-SPEC.md(来自博途内嵌 XSD + TiaUtilities 源码 + 实测)。

规格 JSON 格式
--------------
{
  "tags":  { "启动按钮": "%I0.0", "接触器": "%Q0.0" },   // 可选, 仅用于生成 ensure_tags 命令
  "keep_first_segment": true,                            // 保留基线里的段1(用户手画的)
  "networks": [
    {
      "title": "电机主回路",
      "rungs": [
        [ {"k":"nc","tag":"停止按钮"},
          {"k":"nc","tag":"热保护"},
          {"k":"par","branches":[ {"k":"no","tag":"启动按钮"},
                                  {"k":"no","tag":"接触器"} ]},
          {"k":"coil","tag":"接触器"} ]
      ]
    }
  ]
}

元件类型 k:
  no    常开触点   -| |-
  nc    常闭触点   -|/|-        (Part 内加 <Negated Name="operand"/>)
  coil  线圈       -( )-
  scoil S 线圈     (S)
  rcoil R 线圈     (R)
  par   并联组      { branches: [元件...] }   → 生成 OR 部件(Name="O") + in1/in2/...
  not   取反 NOT

用法
----
  # 1) 只生成 XML(不碰博途)
  python ladgen_cli.py spec.json -o out.xml --base 当前OB1导出.xml

  # 2) 顺便输出建变量命令(交给 worker 的 ensure_tags)
  python ladgen_cli.py spec.json -o out.xml --base cur.xml --emit-tags
"""
import argparse
import json
import os
import sys

from lxml import etree

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ladgen


def ln(t):
    return t.split('}')[-1] if '}' in t else t


# ---------------------------------------------------------------- 规格 -> 元件
def entry_pin_of(el):
    """
    元件"入口"引脚的名字。LAD 里各元件的入口引脚名并不统一:
      触点/线圈/MOVE/SR: in         比较: pre
      数学/转换:          en         定时器: IN(大写)
      计数器:             CU
    并联时必须把上游接到**正确的入口引脚**, 写死 'in' 会报引脚不存在。
    """
    k = el.get('k')
    if k in ('GE', 'GT', 'EQ', 'NE', 'LT', 'LE'):
        return 'pre'
    if k in ('ADD', 'SUB', 'MUL', 'DIV', 'MOD', 'CONV'):
        return 'en'
    if k in ('TON', 'TOF', 'TONR', 'TP'):
        return 'IN'
    if k in ('CTU', 'CTD', 'CTUD'):
        return 'CU'
    return 'in'


def build_branch(nb, branch):
    """
    构建一条并联支路。branch 可以是:
      - 单个元件 dict                  → 一支一个元件
      - 元件 dict 的列表                → 支路内**串联**多个元件
    返回 (入口 uid, 入口引脚名, 末端 uid, 末端输出引脚名)。

    支路内部的串联很常见, 例如 "运行中 AND 正常模式 AND 阶段==0" 这种与项。
    """
    els = branch if isinstance(branch, list) else [branch]
    if not els:
        raise SystemExit('并联支路不能为空')
    entry = None
    entry_pin = 'in'
    prev_uid = None
    prev_out = 'out'
    for i, el in enumerate(els):
        uid, out_pin = build_element(nb, el, feed_from=prev_uid, rail=False,
                                     out_pin=prev_out)
        if i == 0:
            entry = uid
            entry_pin = entry_pin_of(el)
        prev_uid, prev_out = uid, out_pin
    return entry, entry_pin, prev_uid, prev_out


def build_par(nb, spec, feed_from=None):
    """
    并联组。返回 OR 部件的 UId。

    并联的入口 wiring 依赖"上一个元件的 out", 所以这里只生成 OR 部件 +
    支路元件 + 支路出口(inN), 入口线在调用方知道上一个 uid 后再补。
    feed_from: 上一个元件的 uid(为 None 表示从左母线来)。

    每条支路可以是单元件, 也可以是**串联的元件列表**(见 build_branch)。
    """
    branches = spec.get('branches') or []
    if len(branches) < 2:
        raise SystemExit('par 至少需要 2 条支路')
    orp = nb.or_part(len(branches))

    entries = []
    tails = []
    for b in branches:
        e, ep, t, tp = build_branch(nb, b)
        entries.append((e, ep))
        tails.append((t, tp))

    if feed_from is not None:
        # 上一个元件的 out 汇到各支路的入口(必须同一根线); 引脚名按元件类型取
        conns = [(feed_from, 'out')] + entries
        nb.merge(conns)
    else:
        # 从左母线来: 母线线里挂各支路的入口
        nb.left_rail_multi(entries)
    for i, (u, tp) in enumerate(tails):
        nb.series(u, orp, 'in%d' % (i + 1), out_name=tp)
    return orp


def build_element(nb, spec, feed_from=None, rail=True, out_pin='out'):
    """
    rail=False 时不接左母线/上游 —— 用于并联支路内部,
    因为并联支路的入口由 build_par 统一用一根合并线处理。
    out_pin: 从上游元件取电时用哪个输出引脚名。
    local=True 时(FB 内部): 变量名按 **局部引用** 处理(引脚/静态变量),
    而不是 PLC 全局变量表 —— 否则编译报 'Tag "输入" not defined'。
    """
    local = spec.get('local', False)
    lstyle = spec.get('local_style', 'hash')
    if local:
        acc_of = lambda n: nb.access_local(n, lstyle)
    else:
        acc_of = nb.access_tag
    k = spec.get('k')
    tag = spec.get('tag')
    if k == 'no':
        uid = nb.contact_no_scope(tag, local)
    elif k == 'nc':
        uid = nb.contact_nc_scope(tag, local)
    elif k == 'coil':
        uid = nb.coil_scope(tag, local)
    elif k == 'scoil':
        uid = nb.coil_variant(tag, 'SCoil')
    elif k == 'rcoil':
        uid = nb.coil_variant(tag, 'RCoil')
    elif k == 'not':
        uid = nb.part('Not')
    elif k in ('p', 'n'):
        # 上升沿(p)/下降沿(n)。真实 Part 名 PContact / NContact。
        # 引脚 4 个: pre(电源流入口) / operand(信号) / bit(边沿存储位) / out
        uid = nb.part('PContact' if k == 'p' else 'NContact')
        sig = spec.get('tag')
        store = spec.get('store')
        if not sig or not store:
            raise SystemExit('%s 需要 tag(信号变量) 和 store(边沿存储位)' % k)
        if feed_from is not None:
            nb.series(feed_from, uid, 'pre', out_pin)
        elif rail:
            nb.left_rail(uid, 'pre')
        nb.operand_wire(acc_of(sig), uid, 'operand')
        nb.operand_wire(acc_of(store), uid, 'bit')
        return uid, 'out'
    elif k == 'CONV':
        # 类型转换。src/dest 覆盖全部组合(Int->DInt, DInt->Real, Real->Int ...)
        src_t = spec.get('src_type', 'Int')
        dst_t = spec.get('dest_type', 'DInt')
        uid = nb.convert(src_t, dst_t)
        if feed_from is not None:
            nb.series(feed_from, uid, 'en', out_pin)
        elif rail:
            nb.left_rail(uid, 'en')
        v = spec.get('in')
        if isinstance(v, (int, float)):
            nb.operand_wire(nb.literal(v, src_t), uid, 'in')
        elif v:
            nb.operand_wire(acc_of(str(v)), uid, 'in')
        if spec.get('out'):
            nb.pin_wire(uid, 'out', acc_of(spec['out']))
        return uid, 'out'
    elif k in ('GE', 'GT', 'EQ', 'NE', 'LT', 'LE'):
        # 比较运算。博途界面显示 >= > == <> < <=, XML 里是混合大小写 Ge/Gt/Eq/Ne/Lt/Le
        xml_name = {'GE': 'Ge', 'GT': 'Gt', 'EQ': 'Eq', 'NE': 'Ne', 'LT': 'Lt', 'LE': 'Le'}
        stype = spec.get('vtype', 'Int')
        vals = spec.get('values') or []          # [被比较值, 左值, 右值]
        uid = nb.compare(xml_name[k], stype)
        if feed_from is not None:
            nb.series(feed_from, uid, 'pre', out_pin)
        elif rail:
            nb.left_rail(uid, 'pre')
        for i, v in enumerate(vals[:2]):
            if v is None:
                continue
            pin = 'in%d' % (i + 1)
            if isinstance(v, (int, float)):
                # 常量类型跟随比较类型: Real 比较用 Real 常量(30 -> "30.0")
                nb.operand_wire(nb.literal(v, stype), uid, pin)
            else:
                nb.operand_wire(acc_of(str(v)), uid, pin)
        return uid, 'out'
    elif k == 'MOVE':
        outs = spec.get('outs') or []
        uid = nb.move(card=max(1, len(outs)))
        if feed_from is not None:
            nb.series(feed_from, uid, 'en', out_pin)
        elif rail:
            nb.call_enable(uid)
        src = spec.get('in')
        if isinstance(src, (int, float)):
            nb.operand_wire(nb.literal(src, spec.get('vtype', 'Int')), uid, 'in')
        elif src:
            nb.operand_wire(acc_of(str(src)), uid, 'in')
        for i, dst in enumerate(outs):
            nb.pin_wire(uid, 'out%d' % (i + 1), acc_of(dst))
        return uid, 'out'
    elif k in ('SR', 'RS'):
        # 真实 XML 名是 **Sr / Rs**(首字母大写, 不是全大写 SR/RS)
        # 真实接法(博途导出):
        #   母线 -> [触点] -> s      (置位端经过触点)
        #   Access -> r1            (复位端直接接变量, 不过触点)
        #   Access -> operand       ★ 必须! 否则报 "'operand' is not connected"
        # operand 存的是 Q 的状态, 即最终的 Q 输出变量
        uid = nb.flipflop({'SR': 'Sr', 'RS': 'Rs'}[k])
        set_var = spec.get('set')
        reset_var = spec.get('reset')
        q_var = spec.get('q') or reset_var or set_var

        # 真实接法: 两端都从母线经**触点**接入
        #   SR: s=置位, r1=复位(S 优先)   RS: r=复位, s1=置位(R 优先)
        #   operand: 存 Q 状态, 必接
        set_pin = 's' if k == 'SR' else 's1'
        reset_pin = 'r1' if k == 'SR' else 'r'

        rail_uids = []
        if set_var:
            rail_uids.append(nb.contact_no(set_var))
        if reset_var:
            rail_uids.append(nb.contact_no(reset_var))
        if rail_uids:
            nb.left_rail_multi(rail_uids)     # 一段只有一根母线

        i = 0
        if set_var:
            nb.series(rail_uids[i], uid, set_pin, out_name='out'); i += 1
        if reset_var:
            nb.series(rail_uids[i], uid, reset_pin, out_name='out'); i += 1
        if q_var:
            nb.operand_wire(acc_of(q_var), uid, 'operand')
        return uid, 'Q'
    elif k in ('ADD', 'SUB', 'MUL', 'DIV', 'MOD', 'ADD_I', 'SUB_I', 'MUL_I', 'DIV_I',
               'MOD_I', 'ADD_D', 'SUB_D', 'MUL_D', 'DIV_D', 'MOD_D',
               'ADD_R', 'SUB_R', 'MUL_R', 'DIV_R', 'MOD_R'):
        # 博途界面显示全大写(ADD/SUB/...)，XML 里是混合大小写(Add/Sub/...)
        xml_name = {'ADD': 'Add', 'SUB': 'Sub', 'MUL': 'Mul', 'DIV': 'Div', 'MOD': 'Mod'}
        base = k.split('_')[0]
        # 后缀 I/D/R 表示 Int/DInt/Real
        default_type = {'I': 'Int', 'D': 'DInt', 'R': 'Real'}.get(
            k.split('_')[1] if '_' in k else '', 'Int')
        op = xml_name.get(base, base)
        stype = spec.get('vtype', default_type)
        ins = spec.get('inputs') or []
        uid = nb.math_op(op, stype, n_in=max(2, len(ins)))
        # 使能: 前置触点 或 直接母线
        if feed_from is not None:
            nb.series(feed_from, uid, 'en', out_pin)
        elif rail:
            nb.call_enable(uid)
        # 输入引脚(小写 in1/in2/...)
        for i, src in enumerate(ins):
            if isinstance(src, (int, float)):
                acc = nb.literal(src, stype)
            else:
                acc = acc_of(str(src))
            nb.operand_wire(acc, uid, 'in%d' % (i + 1))
        # 输出
        if spec.get('out'):
            nb.pin_wire(uid, 'out', acc_of(spec['out']))
        return uid, 'out'
    elif k in ('TON', 'TOF', 'TONR', 'TP', 'CTR', 'CTU', 'CTD', 'CTUD'):
        inst = spec.get('instance')
        if not inst:
            raise SystemExit('%s 缺少 instance(实例DB名, 如 IEC_Timer_0_DB)' % k)
        is_ctr = k in ('CTU', 'CTD', 'CTUD')
        uid = nb.timer(k, inst, spec.get('vtype') or ('Int' if is_ctr else 'Time'),
                       spec.get('instance_scope', 'GlobalVariable'))
        # 定时器引脚: IN(输入) / Q(输出) / PT / ET  [TONR 另有 R]
        # 计数器引脚: CU 或 CD(计数) / LD(装载) / Q(输出) / PV(预设) / CV(当前) / R(复位)
        cnt_in = {'CTU': 'CU', 'CTD': 'CD', 'CTUD': 'CU'}.get(k, 'CU')
        if is_ctr:
            # 计数端: CTU->CU, CTD->CD, CTUD 两端都有(默认用 CU, 可用 in_pin 指定 CD)
            in_pin = spec.get('in_pin', cnt_in)
            if feed_from is not None:
                nb.series(feed_from, uid, in_pin, out_pin)
            elif rail:
                nb.left_rail(uid, in_pin)
            # PV 预设值(数值字面量或变量)
            pv = spec.get('pv')
            if pv is not None:
                if isinstance(pv, int) or (isinstance(pv, str) and pv.lstrip('-').isdigit()):
                    acc = nb.literal(pv, spec.get('pv_type', 'Int'))
                else:
                    acc = acc_of(str(pv))
                nb.operand_wire(acc, uid, 'PV')
            # CV 当前值: 必须接走, 否则不计数
            if spec.get('cv'):
                nb.pin_wire(uid, 'CV', acc_of(spec['cv']))
            # R 复位 / LD 装载 —— 样板里都是**触点**接的(不是裸变量)
            for key, pin in (('r', 'R'), ('ld', 'LD')):
                var = spec.get(key)
                if not var:
                    continue
                if spec.get(key + '_contact', True):
                    c = nb.contact_no(var)
                    nb.attach_to_existing_rail(c, 'in')
                    nb.series(c, uid, pin, out_name='out')
                else:
                    nb.operand_wire(acc_of(var), uid, pin)
            # CD 端(CTUD 专用): 博途要求必须接上, 否则报
            # "The connection with the name 'CD' is not connected"
            if k == 'CTUD':
                cd_var = spec.get('cd')
                if not cd_var:
                    raise SystemExit('CTUD 必须提供 cd(减计数输入变量)')
                if spec.get('cd_contact', False):
                    c = nb.contact_no(cd_var)
                    nb.attach_to_existing_rail(c, 'in')
                    nb.series(c, uid, 'CD', out_name='out')
                else:
                    nb.operand_wire(acc_of(cd_var), uid, 'CD')
            # CTUD 的两个输出 Q(加到)/QD(减到) 都必须接, 否则逐一报未连接
            for pin, key in (('QD', 'qd'), ('QU', 'qu')):
                v = spec.get(key)
                if v:
                    nb.pin_wire(uid, pin, acc_of(v))
            return uid, 'Q'
        is_tonr = (k == 'TONR')
        if feed_from is not None and not is_tonr:
            nb.series(feed_from, uid, 'IN', out_pin)
        elif rail and not is_tonr:
            nb.left_rail(uid, 'IN')
        pt = spec.get('pt')
        if pt:
            nb.operand_wire(nb.constant(pt), uid, 'PT')
        # ET(已耗时间) 必须接走, 否则博途报:
        #   "The connection with the name 'ET' is not connected to the object ..."
        et_tag = spec.get('et')
        if et_tag:
            nb.pin_wire(uid, 'ET', acc_of(et_tag))
        # TONR 特有: R(复位端)。参考真实样板 —— R 端前面是一个触点(如 %M0.4 Tag_8)。
        # 关键: 复位触点的 in 必须**并进已有那根左母线**, 不能单独再拉一条电源线,
        # 否则博途报 "在 LAD 中, 程序段中只能包含一个电源线"。
        if k == 'TONR':
            if not spec.get('r'):
                raise SystemExit('TONR 必须提供 r(复位触点变量, 如 Tag_8)')
            r_contact = nb.contact_no(spec['r'])
            # 精确复刻真实样板: 一根母线上挂两个 in(主触点 + 复位触点),
            # 各自 out 直接去 TONR 的 IN / R。整段只有**一根**电源线
            # (博途: 一个程序段只能包含一个电源线)。
            if feed_from is not None:
                # 主触点已经接过母线(build_rung 的第一个元件), 这里只把复位触点并进去
                nb.attach_to_existing_rail(r_contact, 'in')
                nb.series(feed_from, uid, 'IN', out_name=out_pin)
            else:
                nb.left_rail_multi([uid, r_contact])
                nb.series(r_contact, uid, 'IN', out_name='out')
            nb.series(r_contact, uid, 'R', out_name='out')
        return uid, 'Q'
    elif k == 'par':
        # OR 部件(O)的输出引脚名是 out —— 不是 'or'
        return build_par(nb, spec, feed_from), 'out'
    else:
        raise SystemExit('未知元件类型: %r' % k)

    if feed_from is not None:
        nb.series(feed_from, uid, 'in', out_pin)
    elif rail:
        nb.left_rail(uid, 'in')
    # 触点/线圈/NOT 等通用元件的输出引脚名都是 out
    return uid, 'out'


def build_call_network(nb, call_spec):
    """
    生成一个只含 CALL 的网络。call_spec:
      fb       : 被调用的 FB 名
      instance : 绑定的背景 DB 名
      enable   : 使能条件(触点变量); 省略则直接接左母线
      params   : [{pin, section, type, to}]  to=接到哪个变量
    """
    call_uid = nb.call(call_spec['fb'], call_spec['instance'],
                       [(p['pin'], p['section'], p.get('type', 'Bool'))
                        for p in call_spec.get('params', [])])

    # 使能端
    enable = call_spec.get('enable')
    if enable:
        en_contact = nb.contact_no(enable)
        nb.left_rail(en_contact)
        nb.series(en_contact, call_uid, 'en')
    else:
        nb.call_enable(call_uid)

    # 引脚接线 (OB 里引用的是 PLC 全局变量表, 所以用 access_tag)
    # 方向: Output 是 引脚->变量(NameCon->IdentCon); Input 和 InOut 都是 变量->引脚
    # (IdentCon->NameCon) —— 依据 the_test FB 真实导出的 CALL, InOut 与 Input 同向。
    for p in call_spec.get('params', []):
        acc = nb.access_tag(p['to'])
        if p['section'] == 'Output':
            nb.call_out(call_uid, p['pin'], acc)
        else:                       # Input / InOut
            nb.call_in(call_uid, p['pin'], acc)
    return nb


def build_rung(nb, rung):
    """一个 rung = 从左母线到最右侧的一串元件。"""
    prev = None
    prev_out = 'out'          # 上一个元件的输出引脚名(触点/线圈 out, 定时器 Q)
    for spec in rung:
        uid, kind = build_element(nb, spec, prev, out_pin=prev_out)
        prev = uid
        prev_out = 'Q' if kind == 'Q' else 'out'
    return prev


# ---------------------------------------------------------------- 组装文档
def make_compile_unit(ol, cu_id, build_fn, take, title):
    cu = etree.SubElement(ol, 'SW.Blocks.CompileUnit')
    cu.set('ID', cu_id)
    cu.set('CompositionName', 'CompileUnits')
    al = etree.SubElement(cu, 'AttributeList')
    nsel = etree.SubElement(al, 'NetworkSource')
    nsel.append(build_fn().to_flgnet())
    etree.SubElement(al, 'ProgrammingLanguage').text = 'LAD'
    col = etree.SubElement(cu, 'ObjectList')
    for comp, text in (('Comment', ''), ('Title', title)):
        mt = etree.SubElement(col, 'MultilingualText')
        mt.set('ID', take()); mt.set('CompositionName', comp)
        mto = etree.SubElement(mt, 'ObjectList')
        mti = etree.SubElement(mto, 'MultilingualTextItem')
        mti.set('ID', take()); mti.set('CompositionName', 'Items')
        ia = etree.SubElement(mti, 'AttributeList')
        etree.SubElement(ia, 'Culture').text = 'zh-CN'
        etree.SubElement(ia, 'Text').text = text


def generate(spec, base_xml, out_xml, emit_tags=False):
    tree = etree.parse(base_xml)
    root = tree.getroot()
    # OB / FB / FC 的外层元素名不同, 统一取第一个块元素
    ob = None
    for el in root:
        if ln(el.tag) in ('SW.Blocks.OB', 'SW.Blocks.FB', 'SW.Blocks.FC'):
            ob = el
            break
    if ob is None:
        raise SystemExit('没找到块元素(期望 SW.Blocks.OB / .FB / .FC)')
    ol = next((e for e in ob if ln(e.tag) == 'ObjectList'), None)
    if ol is None:
        raise SystemExit('没找到 ObjectList')

    if spec.get('keep_first_segment', True):
        cus = [e for e in ol if ln(e.tag) == 'SW.Blocks.CompileUnit']
        for c in cus[1:]:
            ol.remove(c)
        print('保留段1, 清除 %d 个旧段' % (len(cus) - 1))
    else:
        cus = [e for e in ol if ln(e.tag) == 'SW.Blocks.CompileUnit']
        for c in cus:
            ol.remove(c)
        print('清除全部 %d 个旧段' % len(cus))

    used = set()
    for el in root.iter():
        v = el.attrib.get('ID')
        if v:
            try:
                used.add(int(v, 16))
            except ValueError:
                pass
    top = max(used) + 1 if used else 1

    def take():
        nonlocal top
        while top in used:
            top += 1
        v = top
        used.add(v)
        top += 1
        return format(v, 'X')

    alloc = ladgen.UidAllocator(start=100000)

    for net in spec.get('networks', []):
        title = net.get('title', '网络')
        call_spec = net.get('call')

        if call_spec:
            # CALL 网络: 整段就是一次调用
            def builder(a=alloc, cs=call_spec):
                nb = ladgen.NetworkBuilder(a)
                build_call_network(nb, cs)
                return nb

            make_compile_unit(ol, take(), builder, take, title)
            print('  + 生成 CALL: %s' % title)
            continue

        rungs = net.get('rungs') or []
        if not rungs:
            continue

        def builder(a=alloc, rungs=rungs):
            nb = ladgen.NetworkBuilder(a)
            for rung in rungs:
                build_rung(nb, rung)
            return nb

        make_compile_unit(ol, take(), builder, take, title)
        print('  + 生成: %s' % title)

    tree.write(out_xml, encoding='utf-8', xml_declaration=True)
    print('已写入 %s' % out_xml)

    if emit_tags and spec.get('tags'):
        csv = ','.join('%s@%s' % (k, v) for k, v in spec['tags'].items())
        print('\n# 建变量(交给 worker):')
        print('TiaEngine.exe --mode ensure_tags --tags "%s"' % csv)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('spec')
    ap.add_argument('-o', '--out', required=True)
    ap.add_argument('--base', help='基线 OB1 导出 XML(保留段1用)')
    ap.add_argument('--emit-tags', action='store_true')
    a = ap.parse_args()

    with open(a.spec, encoding='utf-8-sig') as f:   # utf-8-sig: 容忍 Windows 编辑器写入的 BOM
        spec = json.load(f)

    if a.base and not os.path.exists(a.base):
        raise SystemExit('基线文件不存在: %s' % a.base)
    generate(spec, a.base, a.out, a.emit_tags)


if __name__ == '__main__':
    main()
