# Stage the freshly built agent as the bundle that new invites embed.
# Does NOT touch appsettings.json, the database, downloaded setups or any running process.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'dist\agent-new\PulseDesk.Agent.exe'
if (-not (Test-Path $src)) { throw "missing $src - build the agent first" }
$newLen = (Get-Item $src).Length

foreach ($dir in @('dist\server\agent', 'dist\server_new\agent')) {
    $targetDir = Join-Path $root $dir
    if (-not (Test-Path $targetDir)) { continue }
    $dst = Join-Path $targetDir 'PulseDesk.Agent.exe'
    $oldLen = if (Test-Path $dst) { (Get-Item $dst).Length } else { 0 }
    if ($oldLen -eq $newLen) { "unchanged $dir ($newLen bytes)"; continue }
    Copy-Item $src $dst -Force
    "staged $dir ($oldLen -> $newLen bytes)"
}

# Cached personalised setups contain the OLD binary. The server rebuilds any cached file whose
# size differs from BundleSize(code), so clear them now to avoid serving a stale agent.
$cache = Join-Path $root 'dist\server\data\downloads'
if (Test-Path $cache) {
    $n = @(Get-ChildItem $cache -Filter '*.exe' -File -EA SilentlyContinue).Count
    if ($n -gt 0) {
        Get-ChildItem $cache -Filter '*.exe' -File -EA SilentlyContinue | Remove-Item -Force
        "cleared $n cached setup(s); next download regenerates them with the new agent"
    }
}
