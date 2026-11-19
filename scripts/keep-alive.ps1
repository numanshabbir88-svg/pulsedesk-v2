# PulseDesk master keep-alive - keeps the whole stack alive WITHOUT a custom domain.
# Keeps alive: admin server (5081, fixed build from dist\server_new), Cloudflare quick tunnel, enrolled Windows agent.
# Self-healing URL: a quick-tunnel hostname can change / be retired by Cloudflare at any time.
# When the public URL stops answering, this script:
#   1) creates a fresh quick tunnel (cloudflared tunnel --url)
#   2) writes the new URL into dist+src appsettings, desktop shortcuts, agent.json, install-link html
#   3) restarts the server so it serves links with the new URL
#   4) re-arms a fresh invite for the configured email (writes it to dist\logs\CURRENT-INVITE.txt)
# IMPORTANT: this is why no custom domain is needed - the URL updates itself.
# Registered as a SYSTEM scheduled task by install-keepalive.ps1.

$ErrorActionPreference = 'Continue'
$root        = Split-Path -Parent $PSScriptRoot
$logDir      = Join-Path $root 'dist\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log         = Join-Path $logDir 'keep-alive.log'
$cf          = "C:\Users\numan\AppData\Local\Microsoft\WinGet\Packages\Cloudflare.cloudflared_Microsoft.Winget.Source_8wekyb3d8bbwe\cloudflared.exe"
# Prefer the freshly published agent in the repo. The copy in Program Files can stay stale
# when UAC/elevation is unavailable; this keeps the enrolled device on the current build.
$agentExe    = Join-Path $root 'dist\server\agent\PulseDesk.Agent.exe'
if (-not (Test-Path $agentExe)) { $agentExe = 'C:\Program Files\PulseDesk\PulseDesk.Agent.exe' }
$agentDir    = Split-Path -Parent $agentExe
# FIXED server only: the old dist\server\PulseDesk.Server.exe is locked by a stale process
# on 5080 and must never be started or killed. The working build lives in server_new.
$serverExe   = Join-Path $root 'dist\server_new\PulseDesk.Server.exe'
$serverDir   = Join-Path $root 'dist\server'
$cfgPath     = Join-Path $root 'dist\server\appsettings.json'
$srcCfgPath  = Join-Path $root 'src\PulseDesk.Server\appsettings.json'
$installHtml = Join-Path $root 'dist\PD-INSTALL-LINK.html'
$inviteEmail = 'chaudhrynuman69@gmail.com'
$inviteFile  = Join-Path $logDir 'CURRENT-INVITE.txt'
$urlFile     = Join-Path $logDir 'CURRENT-URL.txt'

function Log($m) { Add-Content -Path $log -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m" -Encoding utf8 }
function Test-Port5081 { try { return [bool](Get-NetTCPConnection -LocalPort 5081 -State Listen -ErrorAction Stop) } catch { return $false } }

function Start-FixedServer([string]$baseUrl) {
    if (-not (Test-Path $serverExe)) { Log "fixed server exe missing: $serverExe"; return $false }
    if (Test-Port5081) { return $true }
    $args = @('--urls', 'http://0.0.0.0:5081')
    if ($baseUrl) { $args += "--PublicBaseUrl=$baseUrl" }
    Log "  starting fixed server: $serverExe (cwd=$serverDir, args=$($args -join ' '))"
    Start-Process -FilePath $serverExe -ArgumentList $args -WorkingDirectory $serverDir -WindowStyle Hidden `
        -RedirectStandardOutput "$logDir\server.out.log" -RedirectStandardError "$logDir\server.err.log"
    for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 700; if (Test-Port5081) { return $true } }
    return $false
}

function Get-CurrentUrl {
    try {
        $cfg = Get-Content $cfgPath -Raw -EA Stop | ConvertFrom-Json
        return $cfg.PublicBaseUrl
    } catch { return $null }
}

function Test-UrlAlive([string]$u) {
    if (-not $u) { return $false }
    try {
        $r = Invoke-WebRequest -Uri "$u/index.html" -UseBasicParsing -TimeoutSec 12 -EA Stop
        return ($r.StatusCode -eq 200)
    } catch { return $false }
}

function Replace-InFile([string]$path, [string]$oldS, [string]$newS) {
    if (-not (Test-Path $path)) { return }
    $txt = Get-Content $path -Raw
    if ($txt.Contains($oldS)) {
        $txt = $txt.Replace($oldS, $newS)
        [System.IO.File]::WriteAllText((Resolve-Path $path), $txt, (New-Object System.Text.UTF8Encoding($false)))
        Log "    propagated URL -> $path"
    }
}

function New-QuickTunnel {
    # returns new https URL or $null
    if (-not (Test-Path $cf)) { Log "  cloudflared missing: $cf"; return $null }
    Get-Process cloudflared -EA SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    $out = Join-Path $logDir 'tunnel.out.log'
    $err = Join-Path $logDir 'tunnel.err.log'
    Set-Content $out ''; Set-Content $err ''
    $p = Start-Process -FilePath $cf `
        -ArgumentList @('tunnel','--url','http://localhost:5081','--no-autoupdate') `
        -RedirectStandardOutput $out -RedirectStandardError $err -WindowStyle Hidden -PassThru
    for ($i=0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 2
        $txt = (Get-Content $out -Raw -EA SilentlyContinue) + "`n" + (Get-Content $err -Raw -EA SilentlyContinue)
        # Real quick-tunnel hostnames always contain a hyphen (e.g. relief-denver-contests-magical).
        # The loose pattern used to match "https://api.trycloudflare.com" from Cloudflare's status
        # text and waste ~10 minutes retrying a URL that can never serve the dashboard.
        if ($txt -match 'https://[a-z0-9]+(?:-[a-z0-9]+)+\.trycloudflare\.com') { return $Matches[0] }
        if ($p.HasExited) { Log "  cloudflared exited early code=$($p.ExitCode)"; return $null }
    }
    return $null
}

function Apply-NewUrl([string]$newUrl) {
    $old = Get-CurrentUrl
    if (-not $old -or $old -eq $newUrl) { return }
    Log "  URL changed: $old -> $newUrl"
    Replace-InFile $cfgPath $old $newUrl
    Replace-InFile $srcCfgPath $old $newUrl
    Replace-InFile $installHtml $old $newUrl
    Replace-InFile (Join-Path $root 'src\PulseDesk.Mobile\app\src\main\java\com\pulsedesk\mobile\MainActivity.java') $old $newUrl
    Replace-InFile $inviteFile $old $newUrl
    if (Test-Path 'C:\ProgramData\PulseDesk\agent.json') { Replace-InFile 'C:\ProgramData\PulseDesk\agent.json' $old $newUrl }

    # admin shortcuts (Chrome app-mode)
    $sh = New-Object -ComObject WScript.Shell
    foreach ($lnk in @("$env:USERPROFILE\Desktop\PulseDesk Admin.lnk", "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\PulseDesk Admin.lnk")) {
        if (Test-Path $lnk) {
            try { $l = $sh.CreateShortcut($lnk); $l.Arguments = "--app=$newUrl/index.html"; $l.Save() } catch {}
        }
    }

    [System.IO.File]::WriteAllText($urlFile, $newUrl)
    Log "  restarting FIXED 5081 server to load new URL (stale 5080 server is left alone)..."
    Get-NetTCPConnection -LocalPort 5081 -State Listen -EA SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique |
        ForEach-Object { try { Stop-Process -Id $_ -Force; Log "    stopped fixed server pid=$_" } catch {} }
    Start-Sleep -Seconds 3
    if (-not (Start-FixedServer $newUrl)) { Log "  FIXED SERVER DID NOT COME BACK ON 5081" }

    # re-arm a fresh invite so the phone/employee link keeps working on the new URL
    Start-Sleep -Seconds 2
    try {
        $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
        $login = Invoke-RestMethod -Method Post -Uri 'http://localhost:5081/api/admin/login' -TimeoutSec 20 `
            -ContentType 'application/json' -Body (@{ password=$cfg.AdminPassword } | ConvertTo-Json)
        $h = @{ 'X-Admin-Token' = $login.token }
        $inv = Invoke-RestMethod -Method Post -Uri 'http://localhost:5081/api/admin/invite' -Headers $h -TimeoutSec 20 `
            -ContentType 'application/json' -Body (@{ email=$inviteEmail; note='auto new tunnel URL' } | ConvertTo-Json)
        [System.IO.File]::WriteAllText($inviteFile, "INVITE: $($inv.link)`nCODE: $($inv.code)`n")
        Log "  fresh invite: $($inv.link)"
    } catch { Log "  invite re-arm FAIL: $($_.Exception.Message)" }
}

Log "keep-alive started (self-healing no-domain mode)"

while ($true) {
    try {
        # 1) admin server - restart ONLY the fixed 5081 build. Never touch stale 5080 and
        #    never call start-all.ps1 (it starts the locked old exe and would roll back the fix).
        if (-not (Test-Port5081)) {
            Log "fixed server down on 5081 -> starting dist\server_new build"
            if (Start-FixedServer) { Log "  fixed server back on 5081" } else { Log "  FIXED SERVER FAILED TO START" }
        }

        # 2) tunnel: if process dead OR public URL not answering -> new tunnel + propagate
        $cur = Get-CurrentUrl
        $alive = Test-UrlAlive $cur
        if (-not (Get-Process cloudflared -EA SilentlyContinue) -or -not $alive) {
            Log "tunnel/URL unhealthy (alive=$alive, url=$cur) -> creating fresh quick tunnel"
            $newUrl = New-QuickTunnel
            if ($newUrl) {
                Log "  new tunnel URL: $newUrl"
                # wait until DNS/edge reachable, then propagate
                $ready = $false
                for ($i=0; $i -lt 20; $i++) {
                    Start-Sleep -Seconds 2
                    if (Test-UrlAlive $newUrl) { $ready = $true; break }
                }
                if ($ready) { Apply-NewUrl $newUrl }
                else { Log "  new URL not reachable yet; will retry next cycle" }
            } else { Log "  failed to create tunnel" }
        }

        # 3) enrolled Windows agent
        if ((Test-Path $agentExe) -and -not (Get-Process PulseDesk.Agent -EA SilentlyContinue)) {
            Log "agent down -> restarting"
            Start-Process -FilePath $agentExe -WorkingDirectory $agentDir -WindowStyle Hidden
        }
    }
    catch { Log "keep-alive error: $($_.Exception.Message)" }
    Start-Sleep -Seconds 20
}
