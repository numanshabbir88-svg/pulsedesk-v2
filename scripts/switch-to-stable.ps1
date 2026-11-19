# One-time migration: public links/agent/server -> permanent workers.dev URL.
# The quick tunnel stays in CURRENT-ORIGIN.txt as the Worker's hidden upstream.
param(
    [string]$StableUrl = 'https://pulsedesk.pulsedesk-numan.workers.dev',
    [string]$OriginUrl = 'https://horizontal-abs-occurred-feelings.trycloudflare.com'
)
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $root 'dist\logs'
$cfgPath = Join-Path $root 'dist\server\appsettings.json'
$srcCfg = Join-Path $root 'src\PulseDesk.Server\appsettings.json'
$installHtml = Join-Path $root 'dist\PD-INSTALL-LINK.html'
$mobile = Join-Path $root 'src\PulseDesk.Mobile\app\src\main\java\com\pulsedesk\mobile\MainActivity.java'
$inviteFile = Join-Path $logDir 'CURRENT-INVITE.txt'
$originFile = Join-Path $logDir 'CURRENT-ORIGIN.txt'
$legacyUrl = Join-Path $logDir 'CURRENT-URL.txt'
$agentCfg = 'C:\ProgramData\PulseDesk\agent.json'
$agentExe = Join-Path $root 'dist\agent\PulseDesk.Agent.exe'
$serverExe = Join-Path $root 'dist\server_new\PulseDesk.Server.exe'
$serverDir = Join-Path $root 'dist\server'

function Replace([string]$path, [string]$old, [string]$new) {
    if (-not (Test-Path $path)) { return }
    $txt = Get-Content $path -Raw
    if ($txt.Contains($old)) {
        [IO.File]::WriteAllText((Resolve-Path $path), $txt.Replace($old, $new), (New-Object Text.UTF8Encoding($false)))
        "  updated $path"
    }
}

$oldUrl = (Get-Content $cfgPath -Raw | ConvertFrom-Json).PublicBaseUrl
"old public URL: $oldUrl"

# Every public-facing place gets the stable URL; no quick hostname survives.
Replace $cfgPath $oldUrl $StableUrl
Replace $srcCfg $oldUrl $StableUrl
Replace $installHtml $oldUrl $StableUrl
Replace $mobile $oldUrl $StableUrl
Replace $inviteFile $oldUrl $StableUrl
Replace $agentCfg $oldUrl $StableUrl
[IO.File]::WriteAllText($originFile, $OriginUrl)
[IO.File]::WriteAllText($legacyUrl, $OriginUrl)

# Desktop / Start menu shortcuts
$sh = New-Object -ComObject WScript.Shell
foreach ($lnk in @("$env:USERPROFILE\Desktop\PulseDesk Admin.lnk", "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\PulseDesk Admin.lnk")) {
    if (Test-Path $lnk) {
        try { $l = $sh.CreateShortcut($lnk); $l.Arguments = "--app=$StableUrl/index.html"; $l.Save(); "  updated $lnk" } catch {}
    }
}

# Restart server so its invite links use the stable base URL.
$listener = Get-NetTCPConnection -LocalPort 5081 -State Listen -EA SilentlyContinue
$pids = @($listener | Select-Object -ExpandProperty OwningProcess -Unique)
foreach ($pid_ in $pids) {
    try { Stop-Process -Id $pid_ -Force -EA Stop; "  stopped server pid=$pid_" }
    catch { "  could not stop server pid=$pid_ : $($_.Exception.Message)" }
}
if (-not (Get-NetTCPConnection -LocalPort 5081 -State Listen -EA SilentlyContinue)) {
    Start-Process -FilePath $serverExe -ArgumentList @('--urls','http://0.0.0.0:5081',"--PublicBaseUrl=$StableUrl") -WorkingDirectory $serverDir -WindowStyle Hidden
    for ($i=0; $i -lt 30; $i++) { Start-Sleep -Milliseconds 700; if (Get-NetTCPConnection -LocalPort 5081 -State Listen -EA SilentlyContinue) { break } }
    "  server restarted"
} else { "  server already listening; left running" }

# Fresh invite on the stable URL (the DB stores full links).
try {
    $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
    $login = Invoke-RestMethod -Method Post -Uri 'http://localhost:5081/api/admin/login' -TimeoutSec 20 -ContentType 'application/json' -Body (@{ password=$cfg.AdminPassword } | ConvertTo-Json)
    $h = @{ 'X-Admin-Token' = $login.token }
    $inv = Invoke-RestMethod -Method Post -Uri 'http://localhost:5081/api/admin/invite' -Headers $h -TimeoutSec 20 -ContentType 'application/json' -Body (@{ email='chaudhrynuman69@gmail.com'; note='stable workers.dev URL' } | ConvertTo-Json)
    [IO.File]::WriteAllText($inviteFile, "INVITE: $($inv.link)`nCODE: $($inv.code)`n")
    "  fresh invite: $($inv.link)"
} catch { "  invite generation failed: $($_.Exception.Message)" }

# Restart the interactive agent so it reconnects through the stable worker URL.
foreach ($a in @(Get-CimInstance Win32_Process -Filter "Name='PulseDesk.Agent.exe'" -EA SilentlyContinue)) {
    if ($a.SessionId -gt 0) {
        try { Stop-Process -Id $a.ProcessId -Force -EA Stop; "  stopped interactive agent pid=$($a.ProcessId)" } catch {}
    }
}
Start-Sleep -Seconds 2
$p = Start-Process -FilePath $agentExe -ArgumentList '--run' -WorkingDirectory (Split-Path $agentExe) -WindowStyle Hidden -PassThru
"  started interactive agent pid=$($p.Id)"

"DONE public=$StableUrl origin=$OriginUrl"
