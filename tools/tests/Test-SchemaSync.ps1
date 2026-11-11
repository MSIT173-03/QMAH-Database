$ErrorActionPreference = 'Stop'
$script = Join-Path (Split-Path -Parent $PSScriptRoot) 'Sync-Schema.ps1'
$temporaryRoot = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path
$fixtureRoot = Join-Path $temporaryRoot ('qmah-schema-fixture-' + [guid]::NewGuid().ToString('N'))
$engine = (Get-Command pwsh).Source

function Assert-True([bool]$condition, [string]$behavior) {
    if (-not $condition) { throw $behavior }
    Write-Output "PASS $behavior"
}

try {
    $qmah = Join-Path $fixtureRoot 'QMAH'
    $database = Join-Path $fixtureRoot 'QMAH-Database'
    New-Item -ItemType Directory -Path (Join-Path $qmah 'database'), (Join-Path $database 'database') -Force | Out-Null
    $qmahFile = Join-Path $qmah 'database/Schema.sql'
    $databaseFile = Join-Path $database 'database/Schema.sql'
    Set-Content -LiteralPath $qmahFile -Value 'synthetic application fixture' -NoNewline
    Set-Content -LiteralPath $databaseFile -Value 'synthetic database fixture' -NoNewline
    $original = (Get-FileHash -LiteralPath $qmahFile).Hash
    $arguments = @('-NoProfile','-NonInteractive','-File',$script,'-QmahRepositoryPath',$qmah,'-DatabaseRepositoryPath',$database)

    $output = & $engine @arguments 2>&1
    Assert-True ($LASTEXITCODE -ne 0 -and (Get-FileHash -LiteralPath $qmahFile).Hash -eq $original) 'omitting Source fails without overwriting either fixture'

    $output = & $engine @arguments -Source QMAH-Database -WhatIf 2>&1
    Assert-True ($LASTEXITCODE -eq 0 -and (Get-FileHash -LiteralPath $qmahFile).Hash -eq $original) 'WhatIf leaves the destination untouched'

    $output = & $engine @arguments -Source QMAH-Database 2>&1
    Assert-True ($LASTEXITCODE -eq 0 -and (Get-FileHash -LiteralPath $qmahFile).Hash -eq (Get-FileHash -LiteralPath $databaseFile).Hash) 'explicit database source copies only to the application fixture'

    Set-Content -LiteralPath $qmahFile -Value 'synthetic application change' -NoNewline
    $output = & $engine @arguments -Source QMAH 2>&1
    Assert-True ($LASTEXITCODE -eq 0 -and (Get-FileHash -LiteralPath $qmahFile).Hash -eq (Get-FileHash -LiteralPath $databaseFile).Hash) 'explicit application source copies in the reverse direction'
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        $resolved = (Resolve-Path -LiteralPath $fixtureRoot).Path
        $prefix = $temporaryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup escaped the temporary directory.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
