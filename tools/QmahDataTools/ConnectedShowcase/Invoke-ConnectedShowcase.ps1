param(
    [string]$Server = '(localdb)\MSSQLLocalDB',
    [string]$Database = 'QMAH',
    [switch]$Apply,
    [string]$OutputSql
)
$ErrorActionPreference = 'Stop'
# 文案是產品種子資料，執行腳本不需要瀏覽器、測試帳密或外部生成工具。
$answers = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'answers.json') -Raw | ConvertFrom-Json
if ($answers.Count -ne 512 -or @($answers.Id | Sort-Object -Unique).Count -ne 512) { throw '預期 512 件文物，文案件數或 ID 不符。' }
foreach ($answer in $answers) {
    foreach ($field in 'factual', 'fiction', 'creative') {
        if ([string]::IsNullOrWhiteSpace($answer.$field) -or $answer.$field.Length -gt 500 -or -not $answer.$field.Contains("`n")) {
            throw "文案不符合長度或分行規則：$($answer.Id) / $field"
        }
    }
}
$json = ($answers | ConvertTo-Json -Depth 6 -Compress).Replace("'", "''")
$parts = 'begin.sql', 'reconcile-game.sql', 'reconcile-mini.sql', 'reconcile-commerce.sql', 'reconcile-community.sql', 'reconcile-moderation.sql', 'reconcile-collection.sql', 'reconcile-achievements.sql', 'reconcile-progress.sql', 'verify-commerce.sql', 'verify-community.sql', 'finish.sql'
$sql = ($parts | ForEach-Object { Get-Content -LiteralPath (Join-Path $PSScriptRoot $_) -Raw }) -join "`n"
$sql = $sql.Replace('__ANSWERS__', $json)
$communityCopy = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'community-copy.json') -Raw | ConvertFrom-Json
$sql = $sql.Replace('__COMMUNITYCOPY__', ($communityCopy | ConvertTo-Json -Depth 6 -Compress).Replace("'", "''"))
$communityPosts = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'community-post-copy.json') -Raw | ConvertFrom-Json
$sql = $sql.Replace('__COMMUNITYPOSTCOPY__', ($communityPosts | ConvertTo-Json -Depth 6 -Compress).Replace("'", "''"))
if ($OutputSql) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputSql), $sql, [Text.UTF8Encoding]::new($true)) }
if (-not $Apply) { Write-Output '已產生 SQL，未修改資料庫。使用 -Apply 才會套用。'; return }
$temporarySql = Join-Path ([IO.Path]::GetTempPath()) ('qmah-connected-' + [guid]::NewGuid() + '.sql')
try {
    [IO.File]::WriteAllText($temporarySql, $sql, [Text.UTF8Encoding]::new($true))
    & sqlcmd -S $Server -d $Database -b -r 1 -i $temporarySql -W -w 200
    if ($LASTEXITCODE -ne 0) { throw '關聯資料更新失敗，交易已回復。' }
} finally { if (Test-Path -LiteralPath $temporarySql) { Remove-Item -LiteralPath $temporarySql } }
