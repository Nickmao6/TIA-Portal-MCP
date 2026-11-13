"""
手工构造一个"只有一个空段"的 OB1 XML, 用于彻底清空工程。
用途: 当变量被删光、块因引用不存在变量而不一致、无法导出时的破局手段。
依据: 从真实导出的 OB1 结构裁剪而来(见 LAD-SPEC.md 第二节)。
"""
import sys
import io

TPL = '''<?xml version="1.0" encoding="utf-8"?>
<Document>
  <Engineering version="V21" />
  <DocumentInfo>
    <Created>2026-01-01T00:00:00Z</Created>
    <ExportSetting>WithDefaults</ExportSetting>
  </DocumentInfo>
  <SW.Blocks.OB ID="0">
    <AttributeList>
      <AutoNumber>true</AutoNumber>
      <HeaderAuthor />
      <HeaderFamily />
      <HeaderName />
      <HeaderVersion>0.1</HeaderVersion>
      <Interface>
        <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
          <Section Name="Input">
            <Member Name="Initial_Call" Datatype="Bool" Accessibility="Public" Informative="true" />
            <Member Name="Remanence" Datatype="Bool" Accessibility="Public" Informative="true" />
          </Section>
          <Section Name="Temp" />
          <Section Name="Constant" />
        </Sections>
      </Interface>
      <IsIECCheckEnabled>false</IsIECCheckEnabled>
      <MemoryLayout>Optimized</MemoryLayout>
      <Name>Main</Name>
      <Namespace />
      <Number>1</Number>
      <ProgrammingLanguage>LAD</ProgrammingLanguage>
      <SecondaryType>ProgramCycle</SecondaryType>
      <SetENOAutomatically>false</SetENOAutomatically>
    </AttributeList>
    <ObjectList>
      <MultilingualText ID="1" CompositionName="Comment">
        <ObjectList>
          <MultilingualTextItem ID="2" CompositionName="Items">
            <AttributeList>
              <Culture>zh-CN</Culture>
              <Text />
            </AttributeList>
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
                <AttributeList>
                  <Culture>zh-CN</Culture>
                  <Text />
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
          <MultilingualText ID="6" CompositionName="Title">
            <ObjectList>
              <MultilingualTextItem ID="7" CompositionName="Items">
                <AttributeList>
                  <Culture>zh-CN</Culture>
                  <Text />
                </AttributeList>
              </MultilingualTextItem>
            </ObjectList>
          </MultilingualText>
        </ObjectList>
      </SW.Blocks.CompileUnit>
      <MultilingualText ID="8" CompositionName="Title">
        <ObjectList>
          <MultilingualTextItem ID="9" CompositionName="Items">
            <AttributeList>
              <Culture>zh-CN</Culture>
              <Text>"Main Program Sweep (Cycle)"</Text>
            </AttributeList>
          </MultilingualTextItem>
        </ObjectList>
      </MultilingualText>
    </ObjectList>
  </SW.Blocks.OB>
</Document>
'''

if __name__ == '__main__':
    out = sys.argv[1] if len(sys.argv) > 1 else r'ladsim\out\empty_ob.xml'
    with io.open(out, 'w', encoding='utf-8') as f:
        f.write(TPL)
    print('已生成空 OB1: ' + out)