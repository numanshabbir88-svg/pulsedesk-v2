# Run on a laptop where PulseDesk installs but shows "site not reachable".
# Writes a plain-text report to the Desktop. Does not change anything on the machine.
$ErrorActionPreference = 'Continue'
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) 'PulseDesk-Diagnostic.txt'
$lines = New-Object System.Collections.Generic.List[string]

$lines.Add('PulseDesk connection diagnostic - ' + (Get-Date))
$lines.Add('Computer: ' + $env:COMPUTERNAME)
$lines.Add('User: ' + $env:USERNAME)
$lines.Add('OS: ' + (Get-CimInstance Win32_OperatingSystem).Caption)
$lines.Add('')

$hosts = @(
    'https://pulsedesk.pulsedesk-numan.workers.dev',
    'https://pdsbackup.pulsedesk-numan.workers.dev'
)
foreach ($u in $hosts) {
    $lines.Add('===== ' + $u)
    try {
        $ips = [Net.Dns]::GetHostAddresses(([Uri]$u).Host) | Select-Object -First 3 -ExpandProperty IPAddressToString
        $lines.Add('DNS: ' + ($ips -join ', '))
    } catch { $lines.Add('DNS FAIL: ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message) }

    try {
        $r = Invoke-WebRequest -Uri ($u + '/index.html') -UseBasicParsing -TimeoutSec 20
        $lines.Add('HTTPS: ' + [int]$r.StatusCode + ' bytes=' + $r.Content.Length)
    } catch { $lines.Add('HTTPS FAIL: ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message) }
    $lines.Add('')
}

$lines.Add('===== System proxy (WinHTTP)')
try { $lines.Add((netsh winhttp show proxy | Out-String).Trim()) } catch { $lines.Add('proxy check failed') }
$lines.Add('')
$lines.Add('===== System date/time (wrong clock breaks TLS)')
$lines.Add((Get-Date).ToString('O'))
$lines.Add('')
$lines.Add('===== Windows Defender protection history (last 10 events)')
try {
    $ev = Get-WinEvent -FilterHashtable @{ LogName='Microsoft-Windows-Windows Defender/Operational'; StartTime=(Get-Date).AddDays(-7) } -MaxEvents 10 -EA SilentlyContinue
    if ($ev) { foreach ($e in $ev) { $lines.Add($e.TimeCreated.ToString('s') + ' id=' + $e.Id + ' ' + ($e.Message -split "`n")[0]) } }
    else { $lines.Add('no recent Defender events') }
} catch { $lines.Add('Defender log unavailable: ' + $_.Exception.Message) }
$lines.Add('')
$lines.Add('===== Agent log (last 60 lines)')
$log = 'C:\ProgramData\PulseDesk\agent.log'
if (Test-Path $log) { $lines.Add((Get-Content $log -Tail 60 | Out-String).Trim()) }
else { $lines.Add('no agent.log found - PulseDesk did not install yet') }
$lines.Add('')
$lines.Add('===== Install log (last 40 lines)')
$ilog = 'C:\ProgramData\PulseDesk\install.log'
if (Test-Path $ilog) { $lines.Add((Get-Content $ilog -Tail 40 | Out-String).Trim()) }
else { $lines.Add('no install.log found') }

$lines | Set-Content -Path $out -Encoding UTF8
Write-Host "Report saved: $out"
