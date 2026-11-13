param(
  [string]$ToolName = "",
  [string]$ArgsJson = "{}",
  [int]$HoldMs = 4000
)

# Real MCP stdio probe: opens stdin as a live stream (no immediate EOF), sends
# initialize -> initialized -> tools/list [-> tools/call], keeps the pipe open so the
# server has time to flush responses, then reads stdout (protocol) and stderr (logs).

$exe = 'E:\PLCMCP\mcp-server\bin\Debug\net8.0\TiaMcpServer.exe'

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
[void]$proc.Start()

$stdoutTask = $proc.StandardOutput.ReadToEndAsync()
$stderrTask = $proc.StandardError.ReadToEndAsync()

function Send-Line([string]$line) { $proc.StandardInput.WriteLine($line); $proc.StandardInput.Flush() }

Send-Line '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"probe","version":"1.0"}}}'
Start-Sleep -Milliseconds 300
Send-Line '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}'
Start-Sleep -Milliseconds 300
Send-Line '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
Start-Sleep -Milliseconds 500

if ($ToolName -ne "") {
  Send-Line ('{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"' + $ToolName + '","arguments":' + $ArgsJson + '}}')
  Start-Sleep -Milliseconds 800
}

Start-Sleep -Milliseconds $HoldMs
$proc.StandardInput.Close()

if (-not $proc.WaitForExit(30000)) { $proc.Kill($true) }

$stdout = $stdoutTask.GetAwaiter().GetResult()
$stderr = $stderrTask.GetAwaiter().GetResult()

Write-Host "=== STDOUT (protocol) length=$($stdout.Length) ==="
Write-Host $stdout
Write-Host ""
Write-Host "=== STDERR tail ==="
($stderr -split "`r?`n" | Select-Object -Last 4) -join "`n"