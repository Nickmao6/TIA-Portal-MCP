param(
    [switch]$Elevated
)

$ErrorActionPreference = 'Stop'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    if (-not $Elevated) {
        Write-Host "需要管理员权限写入 TIA Openness 白名单，正在请求 UAC..." -ForegroundColor Yellow
        $args = @('-NoProfile','-ExecutionPolicy','Bypass','-File', $PSCommandPath, '-Elevated')
        $p = Start-Process powershell.exe -ArgumentList $args -Verb RunAs -Wait -PassThru
        exit $p.ExitCode
    }
    throw '未获得管理员权限'
}

$toolDir = Split-Path $PSCommandPath -Parent
$root = Split-Path $toolDir -Parent
$targets = @(
    (Join-Path $root 'engine\bin\Debug\TiaEngine.exe'),
    (Join-Path $root 'engine\TiaEngine.exe'),
    (Join-Path $root 'dist\TiaMcpServer-win-x64\engine\TiaEngine.exe')
) | Select-Object -Unique

$backupDir = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = Join-Path $backupDir "OpennessAllowList-backup-$stamp.reg"
& reg.exe export 'HKLM\SOFTWARE\Siemens\Automation\Openness\AllowList' $backup /y | Out-Null
Write-Host "已备份白名单: $backup"

foreach ($path in $targets) {
    if (-not (Test-Path $path)) { Write-Host "跳过不存在: $path" -ForegroundColor DarkYellow; continue }
    $file = Get-Item $path
    $hash = [Convert]::ToBase64String([Security.Cryptography.SHA256]::Create().ComputeHash([IO.File]::ReadAllBytes($path)))
    $date = $file.LastWriteTime.ToString('yyyy/MM/dd HH:mm:ss.fff')
    $key = 'HKLM:\SOFTWARE\Siemens\Automation\Openness\AllowList\TiaEngine.exe'

    $existing = @()
    if (Test-Path $key) {
        $existing = Get-ChildItem $key | ForEach-Object { Get-ItemProperty $_.PSPath }
    }
    if ($existing | Where-Object { $_.Path -eq $path -and $_.FileHash -eq $hash }) {
        Write-Host "已存在，跳过: $path" -ForegroundColor Green
        continue
    }

    $name = 'Entry'
    $n = 1
    while (Test-Path (Join-Path $key $name)) { $name = "Entry ($n)"; $n++ }
    New-Item -Path (Join-Path $key $name) -Force | Out-Null
    Set-ItemProperty -Path (Join-Path $key $name) -Name Path -Value $path
    Set-ItemProperty -Path (Join-Path $key $name) -Name DateModified -Value $date
    Set-ItemProperty -Path (Join-Path $key $name) -Name FileHash -Value $hash
    Write-Host "已加入白名单: $path" -ForegroundColor Green
    Write-Host "  DateModified=$date"
    Write-Host "  FileHash=$hash"
}

Write-Host '完成。重新启动 TIA 后再运行 TiaEngine。' -ForegroundColor Cyan
