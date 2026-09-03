[CmdletBinding()]
param(
    [string]$QmahRepositoryPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

try {
    $databaseRepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

    if ([string]::IsNullOrWhiteSpace($QmahRepositoryPath)) {
        $qmahRepositoryRoot = [IO.Path]::GetFullPath((Join-Path $databaseRepositoryRoot "..\QMAH"))
    } else {
        $qmahRepositoryRoot = [IO.Path]::GetFullPath($QmahRepositoryPath)
    }

    $databaseSchemaPath = Join-Path $databaseRepositoryRoot "database\Schema.sql"
    $qmahSchemaPath = Join-Path $qmahRepositoryRoot "database\Schema.sql"

    if (-not (Test-Path -LiteralPath $databaseSchemaPath -PathType Leaf)) {
        Write-Warning "QMAH-Database 的 Schema.sql 不存在：$databaseSchemaPath"
        exit 0
    }

    if (-not (Test-Path -LiteralPath $qmahRepositoryRoot -PathType Container)) {
        Write-Warning "找不到 QMAH Repository，未執行跨 Repository 比對：$qmahRepositoryRoot"
        exit 0
    }

    if (-not (Test-Path -LiteralPath $qmahSchemaPath -PathType Leaf)) {
        Write-Warning "QMAH 的 Schema.sql 不存在：$qmahSchemaPath"
        exit 0
    }

    $databaseInfo = Get-Item -LiteralPath $databaseSchemaPath
    $qmahInfo = Get-Item -LiteralPath $qmahSchemaPath
    $databaseHash = (Get-FileHash -LiteralPath $databaseSchemaPath -Algorithm SHA256).Hash
    $qmahHash = (Get-FileHash -LiteralPath $qmahSchemaPath -Algorithm SHA256).Hash

    Write-Host "QMAH-Database: $($databaseInfo.Length) bytes, SHA-256 $databaseHash"
    Write-Host "QMAH:          $($qmahInfo.Length) bytes, SHA-256 $qmahHash"

    if ($databaseInfo.Length -ne $qmahInfo.Length -or $databaseHash -ne $qmahHash) {
        Write-Warning "兩個 Repository 的 database\Schema.sql 不一致。請指定來源後執行 Sync-Schema.ps1；本次只提出警告，不阻止後續流程。"
        exit 0
    }

    Write-Host "Schema.sql parity: identical"
    exit 0
} catch {
    Write-Warning "Schema 比對未完成：$($_.Exception.Message)"
    exit 0
}
