param([Parameter(Mandatory=$true)][string]$Binary)
$ErrorActionPreference = 'Stop'
$Binary = (Resolve-Path $Binary).Path
$store = Join-Path ([IO.Path]::GetTempPath()) ("deneb-package-check-" + [guid]::NewGuid())
function Invoke-Deneb([string[]]$CommandArgs) {
    $result = & $Binary @CommandArgs --state-dir $store
    if ($LASTEXITCODE -ne 0) { throw "Packaged CLI command failed: $($CommandArgs -join ' ')" }
    return $result
}
try {
    $version = Invoke-Deneb @('--version')
    if ($version -notmatch '2\.0\.0') { throw 'Unexpected application version' }
    foreach ($language in @('en', 'ru')) {
        New-Item -ItemType Directory -Force -Path $store | Out-Null
        $json = @{ Version = 4; Settings = @{Language = $language}; Jobs = @() } | ConvertTo-Json -Depth 5
        [IO.File]::WriteAllText((Join-Path $store 'state.json'), $json)
        $before = (Get-FileHash (Join-Path $store 'state.json')).Hash
        $help = (Invoke-Deneb @('--help')) -join "`n"
        if ($language -eq 'ru' -and $help -notmatch '[А-Яа-я]') { throw 'Russian resources missing from package' }
        if ($language -eq 'en' -and $help -match '[А-Яа-я]') { throw 'English help contains Russian text' }
        if ($help.Length -lt 100) { throw 'Help output is unexpectedly short' }
        if ((Get-FileHash (Join-Path $store 'state.json')).Hash -ne $before) { throw 'Help changed state' }
    }
    Invoke-Deneb @('start')
    Invoke-Deneb @('start')
    Invoke-Deneb @('pause')
    Invoke-Deneb @('status')
    Invoke-Deneb @('resume')
    Invoke-Deneb @('stop')
    Invoke-Deneb @('stop')
    Write-Output 'PACKAGED CLI PASS: EN/RU resources, read-only help, start/idempotency/pause/status/resume/stop'
} finally {
    & $Binary stop --state-dir $store
}
