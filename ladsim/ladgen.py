#!/usr/bin/env python3
"""
ladgen.py — 按博途真实 SimaticML 结构生成梯形图(LAD)网络。

结构依据：由博途 Openness 导出的真实文件学习而来(samples/Main_OB1_v2.xml)，非凭空发明:
    <SW.Blocks.CompileUnit CompositionName="CompileUnits">   <- 一个"程序段"
      <AttributeList>
        <NetworkSource>
          <FlgNet xmlns=".../NetworkSource/FlgNet/v5">       <- 默认命名空间
            <Parts>  Access(操作数) / Part(触点|线圈) </Parts>
            <Wires>  Powerrail | NameCon("in"/"out"/"operand") | IdentCon </Wires>
真实规律:
  - 常开触点 : <Part Name="Contact" UId="n"/>
  - 线圈     : <Part Name="Coil"   UId="n"/>
  - 左母线   : <Wire><Powerrail/><NameCon UId="触点" Name="in"/></Wire>
  - 操作数   : <Wire><IdentCon UId="Access的UId"/><NameCon UId="触点" Name="operand"/></Wire>
  - 串联     : <Wire><NameCon UId="前" Name="out"/><NameCon UId="后" Name="in"/></Wire>

使用 lxml: 它能正确保留默认命名空间(xmlns=...)。标准库 ElementTree 会把默认命名空间
改写成 ns0: 前缀，导致 TIA 报"无法为父元素添加 ns0:Member"。
"""

from lxml import etree

FLGNET_NS = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5"


def ln(t):
    return t.split('}')[-1] if '}' in t else t

# 常闭触点 <Negated ParaNum="?"/> 的引脚序号候选(1=输入端)
NEG_PARA_NUM = 1


class UidAllocator:
    """
    分配 FlgNet 内部的 UId。
    权威约束(TIA 内嵌 XSD: <xs:attribute name="UId"><xs:restriction base="xs:integer"/>) :
    UId 必须是 **整数**, 不能是十六进制字符串——写成 "100A" 会被 TIA 拒绝:
    "UId 属性无效 - 值 100A 不是有效的 Int32 值"。
    (注意: 文档级 <CompileUnit ID="A"> 那种才是十六进制, 两者不要混用。)
    """

    def __init__(self, start=1000):
        self.n = start

    def next(self):
        v = str(self.n)
        self.n += 1
        return v


class NetworkBuilder:
    """构造一个 CompileUnit(程序段)里的 FlgNet。"""

    def __init__(self, alloc: UidAllocator):
        self.alloc = alloc
        self.parts = etree.Element('Parts')
        self.wires = etree.Element('Wires')

    # ---- Parts ----
    def _access(self, tag_name, scope='GlobalVariable'):
        """
        操作数引用。
        scope='GlobalVariable' : PLC 变量表里的标签(OB 里直接引用)
        scope='Local'          : FB/FC 内部引用自己的引脚或静态变量
        """
        acc = etree.SubElement(self.parts, 'Access')
        acc.set('Scope', scope)
        acc.set('UId', self.alloc.next())
        sym = etree.SubElement(acc, 'Symbol')
        comp = etree.SubElement(sym, 'Component')
        comp.set('Name', tag_name)
        return acc.get('UId')

    def access_local(self, name, style=None):
        """
        FB/FC 内部引用自己的引脚或静态变量。
        权威依据(博途里手画的 FB 导出):
          <Access Scope="LocalVariable" UId="21">
            <Symbol><Component Name="输入"/></Symbol>   ← 纯引脚名, **没有 # 前缀**
          </Access>
        对比 PLC 全局变量: Scope="GlobalVariable"。
        我曾试过 Scope="Local" / 名字加 # 前缀 —— 都报 'Tag "输入" not defined'。
        """
        return self._access(name, scope='LocalVariable')

    def access_tag(self, tag_name):
        """公开版: 建一个变量引用 Access(用于把元件输出引脚接到变量)。"""
        return self._access(tag_name)

    def pin_wire(self, from_uid, from_pin, to_uid):
        """
        把元件某个输出引脚(如 ET / R)连到另一个 Part(如触点)。
        真实导出: <Wire><NameCon UId="TONR" Name="ET"/><IdentCon UId="Access"/></Wire>
        连到触点时目标应写成 <NameCon>。
        """
        w = etree.SubElement(self.wires, 'Wire')
        w.set('UId', self.alloc.next())
        a = etree.SubElement(w, 'NameCon')
        a.set('UId', from_uid)
        a.set('Name', from_pin)
        # 目标可能是 Access(变量)也可能是 Part(触点); 用同一个 uid 引用,
        # 导入端靠两边是否出现在 Parts 里判断。这里统一写成 IdentCon,
        # 若是触点则改为 NameCon(见 pin_wire_to_part)。
        ident = etree.SubElement(w, 'IdentCon')
        ident.set('UId', to_uid)

    def pin_wire_to_part(self, from_uid, from_pin, to_part_uid, to_pin='operand'):
        """把元件的引脚(如 TONR 的 R)连到另一个 Part 的指定端(如触点的 operand)。"""
        w = etree.SubElement(self.wires, 'Wire')
        w.set('UId', self.alloc.next())
        a = etree.SubElement(w, 'NameCon')
        a.set('UId', from_uid)
        a.set('Name', from_pin)
        b = etree.SubElement(w, 'NameCon')
        b.set('UId', to_part_uid)
        b.set('Name', to_pin)

    def _part(self, name, **attrs):
        """建一个 <Part> 并返回它的 UId(字符串)。"""
        p = etree.SubElement(self.parts, 'Part')
        p.set('Name', name)
        p.set('UId', self.alloc.next())
        for k, v in attrs.items():
            p.set(k, v)
        return p.get('UId')

    def contact_no(self, tag):
        acc = self._access(tag)
        uid = self._part('Contact')
        self._operand_wire(acc, uid)
        return uid

    def contact_no_scope(self, tag, local=False):
        """作用域感知的常开触点: local=True 时引用 FB 自己的引脚(LocalVariable)。"""
        acc = self.access_local(tag) if local else self._access(tag)
        uid = self._part('Contact')
        self._operand_wire(acc, uid)
        return uid

    def contact_nc_scope(self, tag, local=False):
        acc = self.access_local(tag) if local else self._access(tag)
        uid = self._part('Contact')
        neg = etree.SubElement(self.parts[-1], 'Negated')
        neg.set('Name', 'operand')
        self._operand_wire(acc, uid)
        return uid

    def coil_scope(self, tag, local=False):
        acc = self.access_local(tag) if local else self._access(tag)
        uid = self._part('Coil')
        self._operand_wire(acc, uid)
        return uid

    def contact_nc(self, tag, pin='operand'):
        """
        常闭触点 -|/|-
        权威依据(TIA 内嵌 LADFBD_v5.xsd, 从 Siemens.Simatic.Lang.BlockLogic.dll 提取):
          <xs:complexType name="Neg_T"><xs:attribute name="Name" .../></xs:complexType>
          <xs:group name="PartSequence_G"> ... <xs:element ref="Negated" .../> ...

        Name 的取值: 博途实测报错信息明确写道——
          "在 LAD 编程语言中，只有在触点的操作数输入处才可进行取反。"
        而操作数输入在连线里叫 "operand"(<NameCon Name="operand"/>),
        所以必须是 <Negated Name="operand"/>, 不是引脚名 "in"。
        """
        acc = self._access(tag)
        uid = self._part('Contact')
        neg = etree.SubElement(self.parts[-1], 'Negated')
        neg.set('Name', pin)
        self._operand_wire(acc, p.get('UId'))
        return p.get('UId')

    def coil(self, tag):
        acc = self._access(tag)
        uid = self._part('Coil')
        self._operand_wire(acc, uid)
        return uid

    # ---- Wires ----
    def _operand_wire(self, access_uid, part_uid, pin='operand'):
        w = etree.SubElement(self.wires, 'Wire')
        w.set('UId', self.alloc.next())
        ident = etree.SubElement(w, 'IdentCon')
        ident.set('UId', access_uid)
        nc = etree.SubElement(w, 'NameCon')
        nc.set('UId', part_uid)
        nc.set('Name', pin)

    def operand_wire(self, access_uid, part_uid, pin):
        """公开版: 接操作数到指定引脚(定时器的 PT 用的是名为 PT 的引脚, 不是 operand)。"""
        self._operand_wire(access_uid, part_uid, pin)

    def left_rail(self, part_uid, pin='in'):
        w = etree.SubElement(self.wires, 'Wire')
        w.set('UId', self.alloc.next())
        etree.SubElement(w, 'Powerrail')
        nc = etree.SubElement(w, 'NameCon')
        nc.set('UId', part_uid)
        nc.set('Name', pin)

    def right_rail(self, part_uid):
        w = etree.SubElement(self.wires, 'Wire')
        w.set('UId', self.alloc.next())
        nc = etree.SubElement(w, 'NameCon')
        nc.set('UId', part_uid)
        nc.set('Name', 'out')
        etree.SubElement(w, 'Powerrail')

    def series(self, out_uid, in_uid, in_name='in', out_name='out'):
        """
        串联: 前件.<out_name> -> 后件.<in_name>。
        触点/线圈用 out_name="out"; 定时器用 "Q"(博途报"connection 'out' does not exist")。
        """
        w = etree.SubElement(self.wires, 'Wire')
        w.set('UId', self.alloc.next())
        a = etree.SubElement(w, 'NameCon')
        a.set('UId', out_uid)
        a.set('Name', out_name)
        b = etree.SubElement(w, 'NameCon')
        b.set('UId', in_uid)
        b.set('Name', in_name)

    def parallel(self, out_uids, in_uid):
        """
        并联: 多个源接到同一个目标 in。
        博途实测限制: "The connection named 'in' at the part with UId '...' is used
        multiple times at the cables." —— 同一个引脚名不能出现在多条 Wire 上,
        所以必须把多个源合并进同一根 Wire 里。
        """
        if not out_uids:
            return
        w = etree.SubElement(self.wires, 'Wire')
        w.set('UId', self.alloc.next())
        for uid in out_uids:
            a = etree.SubElement(w, 'NameCon')
            a.set('UId', uid)
            a.set('Name', 'out')
        b = etree.SubElement(w, 'NameCon')
        b.set('UId', in_uid)
        b.set('Name', 'in')

    def left_rail_multi(self, parts):
        """
        左母线同时接到多个元件的入口(并联支路的公共进线)。
        parts 里每项可以是 uid, 也可以是 (uid, 引脚名) 二元组 —— 因为各元件的
        入口引脚名不统一(触点 in / 比较 pre / 数学 en / 定时器 IN)。
        """
        w = etree.SubElement(self.wires, 'Wire')
        w.set('UId', self.alloc.next())
        etree.SubElement(w, 'Powerrail')
        for item in parts:
            uid, pin = item if isinstance(item, (tuple, list)) else (item, 'in')
            nc = etree.SubElement(w, 'NameCon')
            nc.set('UId', uid)
            nc.set('Name', pin)

    def attach_to_existing_rail(self, part_uid, pin='in'):
        """
        把某个元件的 in 挂到**已有的那根左母线**上, 不新建电源线。
        博途: 一个程序段只能有一个电源线, 所以并联/多入口必须并进同一根。
        """
        for w in self.wires:
            if any(ln(x.tag) == 'Powerrail' for x in w):
                nc = etree.SubElement(w, 'NameCon')
                nc.set('UId', part_uid)
                nc.set('Name', pin)
                return True
        # 没有母线才自己建一根
        self.left_rail(part_uid, pin)
        return False

    def part(self, name, **attrs):
        """通用 Part(如 Not)。"""
        return self._part(name, **attrs)

    def coil_variant(self, tag, part_name):
        """
        S / R 线圈: <Part Name="SCoil"/> / <Part Name="RCoil"/>
        依据 TiaUtilities 的 PartType 枚举 [SimaticEnum("SCoil")] / [SimaticEnum("RCoil")]。
        """
        acc = self._access(tag)
        uid = self._part(part_name)
        self._operand_wire(acc, uid)
        return uid

    def constant(self, value):
        """
        字面量常量(如 T#3s)。依据真实导出:
          <Access Scope="TypedConstant" UId="n">
            <Constant><ConstantValue>T#1000s</ConstantValue></Constant>
          </Access>
        """
        acc = etree.SubElement(self.parts, 'Access')
        acc.set('Scope', 'TypedConstant')
        acc.set('UId', self.alloc.next())
        c = etree.SubElement(acc, 'Constant')
        cv = etree.SubElement(c, 'ConstantValue')
        cv.text = value
        return acc.get('UId')

    def timer(self, kind, instance_name, time_type='Time', instance_scope='GlobalVariable'):
        """
        定时器/计数器等"带实例的功能块部件"。

        依据真实导出(博途里手画的 TON/TOF/TP/TONR/CTU/CTD):
          全局实例(独立实例 DB):
          <Part Name="TON" Version="1.0" UId="n">
            <Instance Scope="GlobalVariable" UId="m">
              <Component Name="IEC_Timer_0_DB"/>
            </Instance>
            <TemplateValue Name="time_type" Type="Type">Time</TemplateValue>
          </Part>

          多重实例(FB 自己的 Static 变量, 不需要外部实例 DB) —— 在干净工程里更实用,
          因为 Openness 无法用系统 FB 名(IEC_TIMER)建实例 DB:
          <Instance Scope="LocalVariable" UId="m">
            <Component Name="阶段定时器"/>
          </Instance>

          计数器(注意模板参数名不同, 且值类型为 Int):
          <TemplateValue Name="value_type" Type="Type">Int</TemplateValue>

        kind            : 'TON'/'TOF'/'TONR'/'TP'  或  'CTU'/'CTD'/'CTUD'
        instance_name   : 实例名(全局实例 DB 名, 或 FB 的 Static 变量名)
        instance_scope  : 'GlobalVariable'(默认) 或 'LocalVariable'(多重实例)
        """
        uid = self._part(kind, Version='1.0')
        part_el = self.parts[-1]
        inst = etree.SubElement(part_el, 'Instance')
        inst.set('Scope', instance_scope)
        inst.set('UId', self.alloc.next())
        comp = etree.SubElement(inst, 'Component')
        comp.set('Name', instance_name)
        tv = etree.SubElement(part_el, 'TemplateValue')
        if kind in ('CTU', 'CTD', 'CTUD'):
            tv.set('Name', 'value_type')
            tv.text = time_type or 'Int'      # 计数器默认 Int
        else:
            tv.set('Name', 'time_type')
            tv.text = time_type or 'Time'
        tv.set('Type', 'Type')
        return uid

    def convert(self, src_type='Int', dest_type='DInt', disable_eno=True):
        """
        类型转换 CONV。依据真实导出(博途里手画的 Int -> DInt):
          <Part Name="Convert" UId="n" DisabledENO="true">
            <TemplateValue Name="SrcType"  Type="Type">Int</TemplateValue>
            <TemplateValue Name="DestType" Type="Type">DInt</TemplateValue>
          </Part>
        引脚(小写): en(使能) / in / out
        **一个元件覆盖所有类型组合** —— 换 SrcType/DestType 的值即可, 不必逐个学。
        常用: Int<->DInt, DInt->Real, Real->Int, Int->Word/DWord/Byte, Time->DInt 等。
        """
        attrs = {}
        if disable_eno:
            attrs['DisabledENO'] = 'true'
        uid = self._part('Convert', **attrs)
        part_el = self.parts[-1]
        for nm, val in (('SrcType', src_type), ('DestType', dest_type)):
            tv = etree.SubElement(part_el, 'TemplateValue')
            tv.set('Name', nm)
            tv.set('Type', 'Type')
            tv.text = val
        return uid

    def compare(self, op, src_type='Int'):
        """
        比较运算。依据真实导出(博途里手画的六种比较):
          Ge(>=) Gt(>) Eq(==) Lt(<) Le(<=) Ne(<>)
        都是**混合大小写**: Ge / Gt / Eq / Lt / Le / Ne
        只有一个 TemplateValue: SrcType
        引脚(小写!): pre(被比较值) / in1(左) / in2(右) / out
        注意: 比较元件**不支持 DisabledENO 属性**(博途报
          "所用 CPU 中的该指令支持属性 DisableENO"),
          且输出**必须接线圈**, 否则报 "需要线圈/分配"。
        """
        uid = self._part(op)          # 不带任何属性
        part_el = self.parts[-1]
        tv = etree.SubElement(part_el, 'TemplateValue')
        tv.set('Name', 'SrcType')
        tv.set('Type', 'Type')
        tv.text = src_type
        return uid

    def move(self, card=1, disable_eno=True):
        """
        MOVE 传送值。依据真实导出:
          <Part Name="Move" UId="25" DisabledENO="true">
            <TemplateValue Name="Card" Type="Cardinality">1</TemplateValue>
          </Part>
        Card = 输出个数(引脚 out1 / out2 / out3…)。
        混合大小写: 界面 MOVE, XML Move。
        """
        attrs = {}
        if disable_eno:
            attrs['DisabledENO'] = 'true'
        uid = self._part('Move', **attrs)
        part_el = self.parts[-1]          # _part 返回 uid, 元素是最后一个
        tv = etree.SubElement(part_el, 'TemplateValue')
        tv.set('Name', 'Card')
        tv.set('Type', 'Cardinality')
        tv.text = str(card)
        return uid

    def flipflop(self, kind):
        """
        SR / RS 双稳态触发器(置位复位)。
        依据真实导出(两者都**没有** TemplateValue):
          SR:  <Part Name="SR"/>  引脚 s(置位) / r1(复位) / Q
          RS:  <Part Name="RS"/>  引脚 r(复位) / s1(置位) / Q
        区别: SR 是 S 优先, RS 是 R 优先。
        """
        return self._part(kind)

    def math_op(self, op, src_type='Int', n_in=2, disable_eno=True):
        """
        数学运算(Add/Sub/Mul/Div/Mod)。
        依据真实导出(博途里手画的五种运算逐个对比):

          Add: <TemplateValue Name="Card" Type="Cardinality">2</TemplateValue>
               <TemplateValue Name="SrcType" Type="Type">Int</TemplateValue>
          Mul: 同 Add, 也有 Card
          Sub / Div / Mod: **没有 Card**, 只有 SrcType

        即 Card(输入个数) 只有 Add 和 Mul 需要; 给 Sub/Div/Mod 加 Card 会被拒:
          "The node 'TemplateValue' with the name 'Card' … is invalid at the element 'Sub'"
        混合大小写: 界面显示 ADD/SUB/..., XML 里是 Add/Sub/...
        引脚: en(小写使能) / in1 / in2(小写) / out
        """
        attrs = {}
        if disable_eno:
            attrs['DisabledENO'] = 'true'
        uid = self._part(op, **attrs)
        part_el = self.parts[-1]
        if op in ('Add', 'Mul'):                 # 只有这两种支持可变输入个数
            tv1 = etree.SubElement(part_el, 'TemplateValue')
            tv1.set('Name', 'Card')
            tv1.set('Type', 'Cardinality')
            tv1.text = str(n_in)
        tv2 = etree.SubElement(part_el, 'TemplateValue')
        tv2.set('Name', 'SrcType')
        tv2.set('Type', 'Type')
        tv2.text = src_type
        return uid

    def call(self, fb_name, instance_db, params):
        """
        CALL 调用 FB。注意: CALL 是**独立的 <Call> 元素**, 不是 <Part>。
        依据真实导出(博途里手画的 CALL):
          <Call UId="24">
            <CallInfo Name="the_test" BlockType="FB">
              <Instance Scope="GlobalVariable" UId="25">
                <Component Name="the_test_DB_1"/>
              </Instance>
              <Parameter Name="输入" Section="Input"  Type="Bool"/>
              <Parameter Name="输出" Section="Output" Type="Bool"/>
              <Parameter Name="输入输出" Section="InOut" Type="Bool"/>
            </CallInfo>
          </Call>
        params: [(引脚名, Section, 类型), ...]  Section ∈ Input/Output/InOut
        """
        c = etree.SubElement(self.parts, 'Call')
        c.set('UId', self.alloc.next())
        info = etree.SubElement(c, 'CallInfo')
        info.set('Name', fb_name)
        info.set('BlockType', 'FB')
        inst = etree.SubElement(info, 'Instance')
        inst.set('Scope', 'GlobalVariable')
        inst.set('UId', self.alloc.next())
        comp = etree.SubElement(inst, 'Component')
        comp.set('Name', instance_db)
        for pname, section, ptype in params:
            p = etree.SubElement(info, 'Parameter')
            p.set('Name', pname)
            p.set('Section', section)
            p.set('Type', ptype)
        return c.get('UId')

    def call_in(self, call_uid, pin_name, access_uid):
        """CALL 的输入引脚: <IdentCon Access/><NameCon Call 引脚名/>"""
        w = etree.SubElement(self.wires, 'Wire')
        w.set('UId', self.alloc.next())
        ident = etree.SubElement(w, 'IdentCon')
        ident.set('UId', access_uid)
        nc = etree.SubElement(w, 'NameCon')
        nc.set('UId', call_uid)
        nc.set('Name', pin_name)

    def call_out(self, call_uid, pin_name, access_uid):
        """CALL 的输出引脚方向相反: <NameCon Call 引脚名/><IdentCon Access/>"""
        w = etree.SubElement(self.wires, 'Wire')
        w.set('UId', self.alloc.next())
        nc = etree.SubElement(w, 'NameCon')
        nc.set('UId', call_uid)
        nc.set('Name', pin_name)
        ident = etree.SubElement(w, 'IdentCon')
        ident.set('UId', access_uid)

    def call_enable(self, call_uid):
        """CALL 的使能端: <Powerrail/><NameCon Call "en"/>(小写)"""
        w = etree.SubElement(self.wires, 'Wire')
        w.set('UId', self.alloc.next())
        etree.SubElement(w, 'Powerrail')
        nc = etree.SubElement(w, 'NameCon')
        nc.set('UId', call_uid)
        nc.set('Name', 'en')

    def literal(self, value, const_type='Int'):
        """
        数值字面量常量(如计数器的 PV = 10)。依据真实导出:
          <Access Scope="LiteralConstant" UId="n">
            <Constant>
              <ConstantType>Int</ConstantType>
              <ConstantValue>10</ConstantValue>
            </Constant>
          </Access>
        注意与定时器的时间常量(TypedConstant + T#3s)不同:
          数值 -> LiteralConstant, 且必须带 ConstantType, 否则报
          "'ConstantValue' has the invalid value '10'"
        """
        acc = etree.SubElement(self.parts, 'Access')
        acc.set('Scope', 'LiteralConstant')
        acc.set('UId', self.alloc.next())
        c = etree.SubElement(acc, 'Constant')
        ct = etree.SubElement(c, 'ConstantType')
        ct.text = const_type
        cv = etree.SubElement(c, 'ConstantValue')
        # Real/LReal 常量必须有小数点: 整数 30 -> "30.0"(否则 TIA 可能按 Int 解析)
        if const_type in ('Real', 'LReal') and isinstance(value, (int, float)):
            cv.text = repr(float(value))
        else:
            cv.text = str(value)
        return acc.get('UId')

    def or_part(self, n_inputs):
        """
        并联(分支)在 SimaticML 里不是"多根线接一个点", 而是**一个 OR 部件**。
        依据 Parozzz/TiaUtilities 的实现 (SimaticML.Blocks.FlagNet.nPart.PartType):
            [SimaticEnum("O")] OR
        以及 SimaticLADSegment.CreateORInputWireConnections:
            compileUnit.CreateWire()
                .CreateNameCon(orPart, orPart.InputConName + (i + 1))   -> "in1","in2",...
                .CreateNameCon(branchPart, branchPart.OutputConName)    -> "out"
        以及 OrPart.GetPart 写入的模板值:
            part.TemplateValue     = "{输入数}"
            part.TemplateValueName = "Card"
            part.TemplateValueType = "Cardinality"
        """
        uid = self._part('O')
        part_el = self.parts[-1]
        tv = etree.SubElement(part_el, 'TemplateValue')
        tv.set('Name', 'Card')
        tv.set('Type', 'Cardinality')
        tv.text = str(n_inputs)
        return uid

    def merge(self, conns):
        """
        把若干个"引脚"合并成同一个电气节点(同一根 Wire 里放多个 NameCon)。
        梯形图并联是"先分后合": 分叉侧要把各支路的 in 汇到一起, 汇合侧要把各支路的
        out 汇到一起, 否则会出现 "connection 'in' is not connected" 错误。
        conns: [(uid, 'in'|'out'), ...]
        """
        if len(conns) < 2:
            return
        w = etree.SubElement(self.wires, 'Wire')
        w.set('UId', self.alloc.next())
        for uid, name in conns:
            a = etree.SubElement(w, 'NameCon')
            a.set('UId', uid)
            a.set('Name', name)

    def branch_origin(self, out_uid):
        """
        并联支路起点: 主干线上标记 <Openbranch/> 并带出 out。
        (TIA 传统 FLG 语义: Openbranch 标记分支从这里分出去)
        """
        w = etree.SubElement(self.wires, 'Wire')
        w.set('UId', self.alloc.next())
        etree.SubElement(w, 'Openbranch')
        a = etree.SubElement(w, 'NameCon')
        a.set('UId', out_uid)
        a.set('Name', 'out')

    def branch_body(self, in_uid, out_uid):
        """支路本体: 自己的 in -> 自己的 out, 用普通 NameCon 连线。"""
        w = etree.SubElement(self.wires, 'Wire')
        w.set('UId', self.alloc.next())
        a = etree.SubElement(w, 'NameCon')
        a.set('UId', in_uid)
        a.set('Name', 'in')
        b = etree.SubElement(w, 'NameCon')
        b.set('UId', out_uid)
        b.set('Name', 'out')

    def branch_rejoin(self, in_uid):
        """
        支路回归点: <OpenCon/> + 目标 in。
        UId 必须唯一(实测 "There are at least two definitions for UIds")。
        """
        w = etree.SubElement(self.wires, 'Wire')
        w.set('UId', self.alloc.next())
        oc = etree.SubElement(w, 'OpenCon')
        oc.set('UId', self.alloc.next())
        b = etree.SubElement(w, 'NameCon')
        b.set('UId', in_uid)
        b.set('Name', 'in')

    def branch_tap(self, hub_uid, out_uid, in_uid):
        """一条并联支路: 从分叉点接出, 接到目标 in。"""
        w = etree.SubElement(self.wires, 'Wire')
        w.set('UId', self.alloc.next())
        oc = etree.SubElement(w, 'OpenCon')
        oc.set('UId', str(hub_uid))
        a = etree.SubElement(w, 'NameCon')
        a.set('UId', out_uid)
        a.set('Name', 'out')
        b = etree.SubElement(w, 'NameCon')
        b.set('UId', in_uid)
        b.set('Name', 'in')

    def to_flgnet(self):
        # 关键: nsmap={None: FLGNET_NS} 生成默认命名空间 xmlns="...", 而不是 ns0: 前缀
        flg = etree.Element('FlgNet', nsmap={None: FLGNET_NS})
        flg.append(self.parts)
        flg.append(self.wires)
        return flg