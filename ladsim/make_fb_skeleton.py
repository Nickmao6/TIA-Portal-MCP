"""生成"只有接口、没有程序段"的 FB 骨架。

用途: 在干净工程里创建带 IEC_TIMER 静态变量的 FB —— 即"多重实例定时器"的载体。
为什么需要: Openness 的 CreateInstanceDB 只认工程里存在的 FB, 用系统 FB 名
(IEC_TIMER) 会报 "Block 'IEC_TIMER' does not exist", 所以干净工程里无法直接建
定时器实例 DB。把定时器声明成 FB 的 Static 变量就能绕开这个限制。

接口定义从 JSON 读: {"Input":[["启动","Bool"]], "Static":[["阶段定时器","IEC_TIMER"]]}

依据: 结构裁剪自博途真实导出的 FB; Section/Member 写法见 LAD-SPEC.md 第十六节。
"""
import sys
import io
import json

TPL = '''<?xml version="1.0" encoding="utf-8"?>
<Document>
  <Engineering version="V21" />
  <DocumentInfo>
    <Created>2026-01-01T00:00:00Z</Created>
    <ExportSetting>WithDefaults</ExportSetting>
  </DocumentInfo>
  <SW.Blocks.FB ID="0">
    <AttributeList>
      <AutoNumber>true</AutoNumber>
      <HeaderAuthor />
      <HeaderFamily />
      <HeaderName />
      <HeaderVersion>0.1</HeaderVersion>
      <Interface>
        <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
{SECTIONS}
        </Sections>
      </Interface>
      <IsIECCheckEnabled>false</IsIECCheckEnabled>
      <IsRetainMemResEnabled>false</IsRetainMemResEnabled>
      <MemoryLayout>Optimized</MemoryLayout>
      <MemoryReserve>100</MemoryReserve>
      <Name>{FBNAME}</Name>
      <Namespace />
      <Number>{NUMBER}</Number>
      <ProgrammingLanguage>LAD</ProgrammingLanguage>
      <SetENOAutomatically>false</SetENOAutomatically>
      <UDABlockProperties />
      <UDAEnableTagReadback>false</UDAEnableTagReadback>
    </AttributeList>
    <ObjectList>
      <MultilingualText ID="1" CompositionName="Comment">
        <ObjectList>
          <MultilingualTextItem ID="2" CompositionName="Items">
            <AttributeList><Culture>zh-CN</Culture><Text /></AttributeList>
          </MultilingualTextItem>
        </ObjectList>
      </MultilingualText>
      <SW.Blocks.CompileUnit ID="3" CompositionName="CompileUnits">
        <AttributeList>
          <NetworkSource />
          <ProgrammingLanguage>LAD</ProgrammingLanguage>
        </AttributeList>
        <ObjectList>
          <MultilingualText ID="4" CompositionName="Comment">
            <ObjectList>
              <MultilingualTextItem ID="5" CompositionName="Items">
                <AttributeList><Culture>zh-CN</Culture><Text /></AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
          <MultilingualText ID="6" CompositionName="Title">
            <ObjectList>
              <MultilingualTextItem ID="7" CompositionName="Items">
                <AttributeList><Culture>zh-CN</Culture><Text /></AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
        </ObjectList>
      </SW.Blocks.CompileUnit>
      <MultilingualText ID="8" CompositionName="Title">
        <ObjectList>
          <MultilingualTextItem ID="9" CompositionName="Items">
            <AttributeList><Culture>zh-CN</Culture><Text /></AttributeList>
          </MultilingualTextItem>
        </ObjectList>
      </MultilingualText>
    </ObjectList>
  </SW.Blocks.FB>
</Document>
'''

MEMBER = '            <Member Name="{name}" Datatype="{dtype}" Remanence="NonRetain" Accessibility="Public" />'

SECTION_ORDER = ['Input', 'Output', 'InOut', 'Static', 'Temp', 'Constant']


def build_sections(pins):
    out = []
    for sec in SECTION_ORDER:
        mems = pins.get(sec) or []
        if not mems:
            out.append('          <Section Name="%s" />' % sec)
        else:
            out.append('          <Section Name="%s">' % sec)
            for m in mems:
                nm, dt = (m[0], m[1]) if isinstance(m, (list, tuple)) else (m['name'], m['type'])
                out.append(MEMBER.format(name=nm, dtype=dt))
            out.append('          </Section>')
    return '\n'.join(out)


def make(out_path, fb_name, number, pins):
    with io.open(out_path, 'w', encoding='utf-8') as f:
        f.write(TPL.replace('{SECTIONS}', build_sections(pins))
                   .replace('{FBNAME}', fb_name)
                   .replace('{NUMBER}', str(number)))
    total = sum(len(pins.get(s) or []) for s in SECTION_ORDER)
    print('已生成 FB 骨架: %s  (%s, 块号%s, %d 个接口成员)' % (out_path, fb_name, number, total))


if __name__ == '__main__':
    out = sys.argv[1]
    name = sys.argv[2]
    num = sys.argv[3]
    pins = json.load(io.open(sys.argv[4], encoding='utf-8-sig')) if len(sys.argv) > 4 else {
        'Input': [('启动', 'Bool')],
        'Output': [('输出', 'Bool')],
        'Static': [('阶段定时器', 'IEC_TIMER'), ('阶段耗时', 'Time')],
    }
    make(out, name, num, pins)