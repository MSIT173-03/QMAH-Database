[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v?\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$ServerInstance = "(localdb)\MSSQLLocalDB",
    [string]$Database = "QMAH",
    [string]$OutputDirectory,
    [string]$RepositorySqlPath,
    [string]$DatabaseRepositoryPath,
    [string]$QmahRepositoryPath,
    [ValidatePattern('^[^\\/:*?"<>|]+$')]
    [string]$RepositorySqlFileName = "QMAH.sql",
    [switch]$KeepTemporaryResources
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$workspaceRoot = (Resolve-Path (Join-Path $repoRoot "..")).Path
$toolProject = Join-Path $PSScriptRoot "QmahDatabaseRelease\QmahDatabaseRelease.csproj"
$qmahRoot = $null
if ($PSBoundParameters.ContainsKey("QmahRepositoryPath")) {
    $qmahRoot = [IO.Path]::GetFullPath($QmahRepositoryPath)
} else {
    $siblingQmahRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "..\QMAH"))
    if (Test-Path -LiteralPath (Join-Path $siblingQmahRoot "QMAH.Web\QMAH.Web.csproj")) {
        $qmahRoot = $siblingQmahRoot
    }
}

if ($qmahRoot -and -not (Test-Path -LiteralPath (Join-Path $qmahRoot "QMAH.Web\QMAH.Web.csproj"))) {
    throw "The QMAH repository path must contain QMAH.Web\QMAH.Web.csproj: $qmahRoot"
}

$webProject = if ($qmahRoot) {
    Join-Path $qmahRoot "QMAH.Web\QMAH.Web.csproj"
} else {
    $null
}
$repositorySqlPathWasProvided = $PSBoundParameters.ContainsKey("RepositorySqlPath")
$databaseRepositoryPathWasProvided = $PSBoundParameters.ContainsKey("DatabaseRepositoryPath")
$repositorySqlFileNameWasProvided = $PSBoundParameters.ContainsKey("RepositorySqlFileName")

if ($repositorySqlPathWasProvided -and
    ($databaseRepositoryPathWasProvided -or $repositorySqlFileNameWasProvided)) {
    throw "Use RepositorySqlPath by itself, or combine DatabaseRepositoryPath with RepositorySqlFileName."
}

if ($repositorySqlPathWasProvided) {
    if ([string]::IsNullOrWhiteSpace($RepositorySqlPath) -or
        $RepositorySqlPath -match '[\\/]$' -or
        $RepositorySqlPath.TrimEnd([char[]]@(' ', '.')) -ne $RepositorySqlPath) {
        throw "RepositorySqlPath must identify a file path, not a directory or a path with a trailing dot or space."
    }

    $repositorySqlLeaf = Split-Path -Leaf $RepositorySqlPath
    if ($repositorySqlLeaf -in @('.', '..') -or
        $repositorySqlLeaf.TrimEnd([char[]]@(' ', '.')) -ne $repositorySqlLeaf) {
        throw "RepositorySqlPath must identify a file path with a valid file name."
    }

    $repositorySql = [IO.Path]::GetFullPath($RepositorySqlPath)
    if (Test-Path -LiteralPath $repositorySql -PathType Container) {
        throw "RepositorySqlPath must identify a file path, not a directory: $repositorySql"
    }
    $databaseRepoRoot = Split-Path -Parent $repositorySql
} else {
    if ([string]::IsNullOrWhiteSpace($DatabaseRepositoryPath)) {
        $databaseRepoRoot = $repoRoot
    } else {
        $databaseRepoRoot = [IO.Path]::GetFullPath($DatabaseRepositoryPath)
    }

    if ([string]::IsNullOrWhiteSpace($RepositorySqlFileName) -or
        $RepositorySqlFileName -in @('.', '..') -or
        $RepositorySqlFileName.TrimEnd([char[]]@(' ', '.')) -ne $RepositorySqlFileName -or
        [IO.Path]::GetFileName($RepositorySqlFileName) -ne $RepositorySqlFileName) {
        throw "RepositorySqlFileName must be one file name without path traversal, dot segments, or trailing dots or spaces."
    }

    $repositorySql = [IO.Path]::GetFullPath((Join-Path $databaseRepoRoot $RepositorySqlFileName))
}

$databaseRepoRoot = [IO.Path]::GetFullPath($databaseRepoRoot)
$repositorySqlParent = [IO.Path]::GetFullPath((Split-Path -Parent $repositorySql))
if (-not [StringComparer]::OrdinalIgnoreCase.Equals($repositorySqlParent, $databaseRepoRoot)) {
    throw "The Snapshot file must remain directly under the selected output directory: $databaseRepoRoot"
}

if (-not (Test-Path -LiteralPath $databaseRepoRoot -PathType Container)) {
    throw "The Snapshot output directory was not found: $databaseRepoRoot. Use -RepositorySqlPath or -DatabaseRepositoryPath to select an existing location."
}
$normalizedVersion = $Version.TrimStart('v')

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $workspaceRoot "_工具輸出\reference-database\$normalizedVersion"
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$workDirectory = Join-Path $OutputDirectory "work"
$databaseFilesDirectory = Join-Path $workDirectory "database-files"
$sourceSnapshot = Join-Path $workDirectory "source-snapshot.bak"
$releaseBackup = Join-Path $OutputDirectory "QMAH-$normalizedVersion.bak"
$releaseSql = Join-Path $OutputDirectory "QMAH-$normalizedVersion.sql"
$determinismSql = Join-Path $workDirectory "determinism.sql"
$validationSql = Join-Path $workDirectory "validation.sql"
$parityReport = Join-Path $OutputDirectory "parity-report.json"
$dataScanReport = Join-Path $OutputDirectory "data-scan.json"
$checksumFile = Join-Path $OutputDirectory "SHA256SUMS.txt"
$webLog = Join-Path $workDirectory "web-startup.log"
$webErrorLog = Join-Path $workDirectory "web-startup-error.log"

$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$localDbInstance = "QMAHRelease_$suffix"
$validationDatabase = "QMAH_Validation_$suffix"
$releaseServer = "(localdb)\$localDbInstance"
$sourceConnection = "Server=$ServerInstance;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True"
$releaseMasterConnection = "Server=$releaseServer;Database=master;Trusted_Connection=True;TrustServerCertificate=True"
$releaseConnection = "Server=$releaseServer;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True"
$validationConnection = "Server=$releaseServer;Database=$validationDatabase;Trusted_Connection=True;TrustServerCertificate=True"

$sqlcmd = (Get-Command sqlcmd -ErrorAction Stop).Source
$sqllocaldb = (Get-Command sqllocaldb -ErrorAction Stop).Source
$webProcess = $null
$localDbCreated = $false

function Resolve-LocalDbServer([string]$Server) {
    if ($Server -notmatch '^\(localdb\)\\(?<Instance>[^\\]+)$') {
        return $Server
    }

    $instanceName = $Matches.Instance
    $info = & $sqllocaldb info $instanceName
    if ($LASTEXITCODE -ne 0) {
        throw "LocalDB instance '$instanceName' could not be inspected."
    }

    $pipeLine = $info | Select-String -SimpleMatch 'Instance pipe name:'
    $pipe = if ($pipeLine) { ($pipeLine.Line -split ':', 2)[1].Trim() } else { $null }
    if ([string]::IsNullOrWhiteSpace($pipe)) {
        & $sqllocaldb start $instanceName | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "LocalDB instance '$instanceName' could not be started."
        }

        $info = & $sqllocaldb info $instanceName
        $pipeLine = $info | Select-String -SimpleMatch 'Instance pipe name:'
        $pipe = if ($pipeLine) { ($pipeLine.Line -split ':', 2)[1].Trim() } else { $null }
    }

    if ([string]::IsNullOrWhiteSpace($pipe)) {
        throw "LocalDB instance '$instanceName' did not expose a named pipe."
    }

    return $pipe
}

$ServerInstance = Resolve-LocalDbServer $ServerInstance
$sourceConnection = "Server=$ServerInstance;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True"

function ConvertTo-SqlIdentifier([string]$Value) {
    return "[" + $Value.Replace("]", "]]") + "]"
}

function ConvertTo-SqlLiteral([string]$Value) {
    return "N'" + $Value.Replace("'", "''") + "'"
}

function Invoke-Sql([string]$Server, [string]$TargetDatabase, [string]$Query) {
    & $sqlcmd -b -S $Server -d $TargetDatabase -Q $Query
    if ($LASTEXITCODE -ne 0) {
        throw "SQL command failed against $Server / $TargetDatabase."
    }
}

function Invoke-ReleaseTool([string[]]$Arguments) {
    & dotnet run --project $toolProject --configuration Release --no-build -- @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "QmahDatabaseRelease failed: $($Arguments[0])"
    }
}

function Remove-DatabaseIfPresent([string]$Server, [string]$Name) {
    $identifier = ConvertTo-SqlIdentifier $Name
    $literal = ConvertTo-SqlLiteral $Name
    Invoke-Sql $Server "master" "IF DB_ID($literal) IS NOT NULL BEGIN ALTER DATABASE $identifier SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE $identifier; END"
}

function New-ValidationScript([string]$SourcePath, [string]$DestinationPath, [string]$TargetDatabase) {
    $content = [IO.File]::ReadAllText($SourcePath)
    $content = $content.Replace("DB_ID(N'$Database')", "DB_ID(N'$TargetDatabase')")
    $content = $content.Replace("Database $Database already exists.", "Database $TargetDatabase already exists.")
    $content = $content.Replace("CREATE DATABASE $(ConvertTo-SqlIdentifier $Database)", "CREATE DATABASE $(ConvertTo-SqlIdentifier $TargetDatabase)")
    $content = $content.Replace("ALTER DATABASE $(ConvertTo-SqlIdentifier $Database)", "ALTER DATABASE $(ConvertTo-SqlIdentifier $TargetDatabase)")
    $content = $content.Replace("USE $(ConvertTo-SqlIdentifier $Database)", "USE $(ConvertTo-SqlIdentifier $TargetDatabase)")
    [IO.File]::WriteAllText($DestinationPath, $content, [Text.UTF8Encoding]::new($false))
}

function Test-WebStartup([string]$ConnectionString) {
    $port = Get-Random -Minimum 52000 -Maximum 59000
    $url = "http://127.0.0.1:$port"
    $oldConnection = $env:ConnectionStrings__QmahDatabase
    $oldEnvironment = $env:ASPNETCORE_ENVIRONMENT
    try {
        $env:ConnectionStrings__QmahDatabase = $ConnectionString
        $env:ASPNETCORE_ENVIRONMENT = "Development"
        $script:webProcess = Start-Process dotnet -ArgumentList @(
            "run", "--project", (Join-Path $repoRoot "QMAH.Web\QMAH.Web.csproj"),
            "--configuration", "Release", "--no-build", "--urls", $url
        ) -RedirectStandardOutput $webLog -RedirectStandardError $webErrorLog -PassThru -WindowStyle Hidden

        $started = $false
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            if ($script:webProcess.HasExited) {
                break
            }
            try {
                $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2
                if ($response.StatusCode -eq 200) {
                    $started = $true
                    break
                }
            }
            catch {
                Start-Sleep -Milliseconds 500
            }
        }

        if (-not $started) {
            $details = @(
                if (Test-Path $webLog) { Get-Content -Raw $webLog }
                if (Test-Path $webErrorLog) { Get-Content -Raw $webErrorLog }
            ) -join [Environment]::NewLine
            if ([string]::IsNullOrWhiteSpace($details)) { $details = "No startup log was produced." }
            throw "QMAH.Web did not start against the SQL-rebuilt database.`n$details"
        }
    }
    finally {
        if ($script:webProcess -and -not $script:webProcess.HasExited) {
            Stop-Process -Id $script:webProcess.Id -Force
            $script:webProcess.WaitForExit()
        }
        $script:webProcess = $null
        $env:ConnectionStrings__QmahDatabase = $oldConnection
        $env:ASPNETCORE_ENVIRONMENT = $oldEnvironment
    }
}

try {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $workDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $databaseFilesDirectory -Force | Out-Null

    Write-Host "[1/10] Building the release tool and optional Web startup target"
    & dotnet build $toolProject --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "Release tool build failed."
    }
    if ($webProject) {
        & dotnet build $webProject --configuration Release --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "QMAH.Web release build failed."
        }
    } else {
        Write-Host "QMAH.Web validation target not found; database-only validation will continue."
    }

    Write-Host "[2/10] Checking the canonical database"
    Invoke-Sql $ServerInstance "master" "IF DB_ID($(ConvertTo-SqlLiteral $Database)) IS NULL THROW 51000, 'Canonical database was not found.', 1;"

    $escapedSnapshot = $sourceSnapshot.Replace("'", "''")
    Invoke-Sql $ServerInstance "master" "BACKUP DATABASE $(ConvertTo-SqlIdentifier $Database) TO DISK = N'$escapedSnapshot' WITH COPY_ONLY, INIT, CHECKSUM, STATS = 10;"

    Write-Host "[3/10] Restoring one isolated canonical snapshot"
    & $sqllocaldb create $localDbInstance | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not create temporary LocalDB instance." }
    $localDbCreated = $true
    & $sqllocaldb start $localDbInstance | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not start temporary LocalDB instance." }

    $releaseServer = Resolve-LocalDbServer "(localdb)\$localDbInstance"
    $releaseMasterConnection = "Server=$releaseServer;Database=master;Trusted_Connection=True;TrustServerCertificate=True"
    $releaseConnection = "Server=$releaseServer;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True"
    $validationConnection = "Server=$releaseServer;Database=$validationDatabase;Trusted_Connection=True;TrustServerCertificate=True"

    Invoke-ReleaseTool @(
        "restore-backup", "--connection", $releaseMasterConnection,
        "--backup", $sourceSnapshot, "--database", $Database,
        "--data-directory", $databaseFilesDirectory
    )

    $diagramCleanup = @"
USE $(ConvertTo-SqlIdentifier $Database);
DROP PROCEDURE IF EXISTS [dbo].[sp_alterdiagram];
DROP PROCEDURE IF EXISTS [dbo].[sp_creatediagram];
DROP PROCEDURE IF EXISTS [dbo].[sp_dropdiagram];
DROP PROCEDURE IF EXISTS [dbo].[sp_helpdiagramdefinition];
DROP PROCEDURE IF EXISTS [dbo].[sp_helpdiagrams];
DROP PROCEDURE IF EXISTS [dbo].[sp_renamediagram];
DROP PROCEDURE IF EXISTS [dbo].[sp_upgraddiagrams];
DROP FUNCTION IF EXISTS [dbo].[fn_diagramobjects];
DROP TABLE IF EXISTS [dbo].[sysdiagrams];
"@
    Invoke-Sql $releaseServer "master" $diagramCleanup

    Write-Host "[4/10] Scanning canonical data boundaries"
    Invoke-ReleaseTool @("scan-data", "--connection", $releaseConnection, "--report", $dataScanReport)

    Write-Host "[5/10] Creating and verifying the release backup"
    $escapedReleaseBackup = $releaseBackup.Replace("'", "''")
    Invoke-Sql $releaseServer "master" "BACKUP DATABASE $(ConvertTo-SqlIdentifier $Database) TO DISK = N'$escapedReleaseBackup' WITH COPY_ONLY, INIT, CHECKSUM, STATS = 10;"
    Invoke-Sql $releaseServer "master" "RESTORE VERIFYONLY FROM DISK = N'$escapedReleaseBackup' WITH CHECKSUM;"

    Write-Host "[6/10] Exporting deterministic full SQL"
    Invoke-ReleaseTool @("export-sql", "--connection", $releaseConnection, "--database", $Database, "--output", $releaseSql)
    Invoke-ReleaseTool @("export-sql", "--connection", $releaseConnection, "--database", $Database, "--output", $determinismSql)
    $firstHash = (Get-FileHash -Algorithm SHA256 $releaseSql).Hash
    $secondHash = (Get-FileHash -Algorithm SHA256 $determinismSql).Hash
    if ($firstHash -ne $secondHash) {
        throw "Two exports from the unchanged canonical snapshot were not byte-identical."
    }

    Write-Host "[7/10] Rebuilding a new database from the SQL file only"
    New-ValidationScript $releaseSql $validationSql $validationDatabase
    # 新版 sqlcmd 不再接受舊版的 -f 參數；完整 SQL 已由 exporter 以 UTF-8 無 BOM 輸出。
    & $sqlcmd -b -S $releaseServer -d master -i $validationSql
    if ($LASTEXITCODE -ne 0) {
        throw "The full SQL file could not rebuild a clean database."
    }

    Write-Host "[8/10] Comparing schema and ordered table data"
    Invoke-ReleaseTool @(
        "compare", "--source", $releaseConnection,
        "--target", $validationConnection, "--report", $parityReport
    )

    Write-Host "[9/10] Validating QmahDbContext and optional QMAH.Web startup"
    Invoke-ReleaseTool @("validate-ef", "--connection", $validationConnection)
    if ($webProject) {
        Test-WebStartup $validationConnection
    } else {
        Write-Host "QMAH.Web startup validation skipped because QmahRepositoryPath was not supplied and no sibling QMAH repository was found."
    }

    Write-Host "[10/10] Publishing the verified repository snapshot"
    Copy-Item -LiteralPath $releaseSql -Destination $repositorySql -Force
    $backupHash = (Get-FileHash -Algorithm SHA256 $releaseBackup).Hash
    $sqlHash = (Get-FileHash -Algorithm SHA256 $releaseSql).Hash
    @(
        "$backupHash *$(Split-Path $releaseBackup -Leaf)",
        "$sqlHash *$(Split-Path $releaseSql -Leaf)"
    ) | Set-Content -LiteralPath $checksumFile -Encoding utf8NoBOM

    Write-Host "Release artifacts are ready:"
    Write-Host "  $releaseBackup"
    Write-Host "  $releaseSql"
    Write-Host "  $checksumFile"
    Write-Host "Repository SQL updated: $repositorySql"
}
finally {
    if ($webProcess -and -not $webProcess.HasExited) {
        Stop-Process -Id $webProcess.Id -Force -ErrorAction SilentlyContinue
    }

    if ($localDbCreated -and -not $KeepTemporaryResources) {
        try { Remove-DatabaseIfPresent $releaseServer $validationDatabase } catch { Write-Warning $_ }
        try { Remove-DatabaseIfPresent $releaseServer $Database } catch { Write-Warning $_ }
        & $sqllocaldb stop $localDbInstance -k 2>$null | Out-Null
        & $sqllocaldb delete $localDbInstance 2>$null | Out-Null
        Remove-Item -LiteralPath $workDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
