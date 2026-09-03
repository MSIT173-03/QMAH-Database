[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet("QMAH", "QMAH-Database")]
    [string]$Source = "QMAH-Database",

    [string]$QmahRepositoryPath,
    [string]$DatabaseRepositoryPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

try {
    if ([string]::IsNullOrWhiteSpace($DatabaseRepositoryPath)) {
        $databaseRepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    } else {
        $databaseRepositoryRoot = [IO.Path]::GetFullPath($DatabaseRepositoryPath)
    }

    if ([string]::IsNullOrWhiteSpace($QmahRepositoryPath)) {
        $qmahRepositoryRoot = [IO.Path]::GetFullPath((Join-Path $databaseRepositoryRoot "..\QMAH"))
    } else {
        $qmahRepositoryRoot = [IO.Path]::GetFullPath($QmahRepositoryPath)
    }

    $sourcePath = if ($Source -eq "QMAH") {
        Join-Path $qmahRepositoryRoot "database\Schema.sql"
    } else {
        Join-Path $databaseRepositoryRoot "database\Schema.sql"
    }

    $destinationPath = if ($Source -eq "QMAH") {
        Join-Path $databaseRepositoryRoot "database\Schema.sql"
    } else {
        Join-Path $qmahRepositoryRoot "database\Schema.sql"
    }

    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        Write-Warning "同步來源不存在：$sourcePath"
        exit 0
    }

    $destinationDirectory = Split-Path -Parent $destinationPath
    if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
        Write-Warning "同步目標資料夾不存在：$destinationDirectory"
        exit 0
    }

    if (Test-Path -LiteralPath $destinationPath -PathType Leaf) {
        $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
        $destinationHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash
        if ($sourceHash -eq $destinationHash) {
            Write-Host "Schema.sql 已一致，不需要同步。"
            exit 0
        }
    }

    if ($PSCmdlet.ShouldProcess($destinationPath, "以 $sourcePath 覆寫")) {
        Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
        Write-Host "已同步：$destinationPath"
    }
    exit 0
} catch {
    Write-Warning "Schema 同步未完成：$($_.Exception.Message)"
    exit 0
}
