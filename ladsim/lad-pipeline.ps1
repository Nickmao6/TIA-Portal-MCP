# ladsim\lad-pipeline.ps1 — LAD 梯形图工作流一键执行
#
# 把整条链路串起来:
#   1. 导出当前块作为基线(保留你手画的段1)
#   2. 按 JSON 规格生成 LAD XML
#   3. 建变量(中文名 -> 地址)
#   4. 导入博途
#   5. 编译并回读完整诊断
#   6. 解读梯形图 + 自动查重复驱动
#
# 用法:
#   powershell -File lad-pipeline.ps1 -Spec spec_motor.json -Block Main -Plc PLC_1
#
# 参数:
#   -Spec   规格 JSON(默认 spec_motor.json)
#   -Block  目标块名(默认 Main / OB1)
#   -Plc    PLC 名(默认 PLC_1)
#   -DryRun 只预演, 不导入不编译

param(
  [string]$Spec = "$PSScriptRoot\spec_motor.json",
  [string]$Block = 'Main',
  [string]$Plc = 'PLC_1',
  [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$py   = if ($env:PLCMCP_PYTHON) { $env:PLCMCP_PYTHON } else { (Get-Command python -ErrorAction SilentlyContinue).Source }
$eng  = Join-Path (Split-Path $PSScriptRoot -Parent) 'engine\bin\Debug\TiaEngine.exe'
$work = "$PSScriptRoot\out"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$env:PYTHONIOENCODING = 'utf-8'

function Step($n) { Write-Host "`n=== $n ===" -ForegroundColor Cyan }
function RunEngine([string[]]$a) {
  $o = & $eng @a 2>$null
  Write-Host $o
  $o
}

if (-not (Test-Path $eng)) { throw "找不到 worker: $eng (请先编译 engine)" }
if (-not (Test-Path $py))  { throw "找不到 Python: $py" }

# 1) 导出基线
Step "1/6 导出基线 ($Block)"
$base = Join-Path $work 'base.xml'
Remove-Item $base -Force -ErrorAction SilentlyContinue
$null = RunEngine @('--mode','export_block','--block',$Block,'--out',$base,'--plc',$Plc)

# 2) 生成 XML
Step "2/6 生成 LAD XML"
$xml = Join-Path $work 'lad.xml'
& $py "$PSScriptRoot\ladgen_cli.py" $Spec -o $xml --base $base --emit-tags
if ($LASTEXITCODE -ne 0) { throw '生成失败' }

if ($DryRun) {
  Step 'dry-run 模式, 到此为止(未导入博途)'
  exit 0
}

# 3) 建变量: 从规格里的 tags 生成 ensure_tags 命令
Step '3/6 建变量'
$specJson = Get-Content $Spec -Raw -Encoding UTF8 | ConvertFrom-Json
if ($specJson.tags) {
  $csv = ($specJson.tags.PSObject.Properties | ForEach-Object { "$($_.Name)@$($_.Value)" }) -join ','
  $null = RunEngine @('--mode','ensure_tags','--tags',$csv,'--plc',$Plc)
}

# 4) 导入 (必须显式 --overwrite: 默认已改为"同名块存在则拒绝", 不再静默覆盖)
Step '4/6 导入 LAD'
$null = RunEngine @('--mode','import_lad_xml','--xml',$xml,'--plc',$Plc,'--overwrite')

# 5) 编译
Step '5/6 编译'
$res = RunEngine @('--mode','compile_plc','--plc',$Plc)
if ($res -match '"errorCount":([1-9]\d*)') {
  Write-Host "`n编译有错误, 详见上面的完整诊断(Severity/Path/描述均未截断)" -ForegroundColor Red
  exit 1
}
if ($res -match '"ok":true') {
  Write-Host "`n编译通过" -ForegroundColor Green
}

# 6) 解读 + 重复驱动检查
Step '6/6 梯形图解读与检查'
$after = Join-Path $work 'after.xml'
Remove-Item $after -Force -ErrorAction SilentlyContinue
$res2 = RunEngine @('--mode','export_block','--block',$Block,'--out',$after,'--plc',$Plc)
if ($res2 -notmatch '"ok":true') {
  Write-Host "`n回导出失败, 无法做最后检查(不影响博途里的内容)" -ForegroundColor Yellow
  exit 1
}
& $py "$PSScriptRoot\analyze_ladder.py" $after

# 7) 逻辑仿真验证(不依赖 PLCSIM, 直接在本机跑梯形图逻辑)
Step '7/7 逻辑仿真验证'
$sim = & $py "$PSScriptRoot\lad_logic_sim.py" $after
$sim | Write-Host
if ($LASTEXITCODE -ne 0) {
  Write-Host "`n!! 逻辑仿真未通过 —— 梯形图已导入但行为不符合预期, 请按上面提示检查触点类型" -ForegroundColor Red
  exit 1
}

Write-Host "`n全部通过: 导入成功 + 编译 0 错误 + 无重复驱动 + 逻辑仿真正确" -ForegroundColor Green