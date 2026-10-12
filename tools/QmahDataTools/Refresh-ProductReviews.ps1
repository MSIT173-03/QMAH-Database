[CmdletBinding()]
param([string]$ServerInstance='.', [string]$Database='QMAH', [int]$Seed=173102, [string]$SqlOutPath)
# db-v1.0.2：重寫全部商品評論文字，並把評論者隨機分配給所有現有帳號（同商品每個帳號最多一則）。
# 星等、狀態、建立時間不動。文字一律不含分號。
$ErrorActionPreference='Stop'
function Read-JsonRows([string]$Query) {
    $result=& sqlcmd -S $ServerInstance -E -C -b -d $Database -y 0 -w 65535 -Q "SET NOCOUNT ON; $Query FOR JSON PATH;"
    if($LASTEXITCODE -ne 0){throw 'Could not read review input.'}
    $json=($result | ForEach-Object {$_.Trim()}) -join ''
    return $json.Substring($json.IndexOf('[')) | ConvertFrom-Json
}
$reviews=@(Read-JsonRows 'SELECT r.Id,r.ProductId,r.Rating,a.Name AS Artifact FROM store.ProductReviews r JOIN store.Products p ON p.Id=r.ProductId LEFT JOIN catalog.Artifacts a ON a.Id=p.ArtifactId ORDER BY r.ProductId,r.CreatedAt,r.Id')
$users=@(Read-JsonRows 'SELECT Id FROM [user].AspNetUsers ORDER BY Id')
if($users.Count -lt 7){throw 'At least seven accounts are required.'}
$random=[Random]::new($Seed)
function Pick($list){ $list[$random.Next($list.Count)] }

$posOpen=@('收到了，整體比我想的還要好。','拆開之後第一個反應是：嗯，沒買錯。','包裝很用心，拿在手上也有分量。','朋友看到都問我在哪買的。','印刷細節很清楚，連小地方都看得到。','色澤很舒服，不會太鮮豔。','放在桌上整個氣氛都不一樣了。','買來自己收藏，越看越喜歡。','實品跟商品頁的照片很接近。','一拆開就有種安心感，做工看起來很紮實。','質感比我預期的好，價格也合理。','這次買得很開心。','拿到的時候有點驚喜。','整體看起來很精緻，不像隨便做做。','圖案很耐看，擺著不膩。','已經是第二次回購了。','出貨速度剛好，沒有讓我等太久。','我是看了圖鑑才下單的，收到後更喜歡了。','比照片更好看一點，光線下顏色很溫潤。','買給家人當禮物，對方說很喜歡。')
$posDetail=@('明信片紙張挺，邊角也沒有壓傷。','我把它放進相框，大小剛好。','夾在書裡當書籤也很好看。','背面的文字排得很整齊，讀起來不吃力。','複製品的比例拿捏得不錯。','放在書架一角，打光之後特別好看。','紙質摸起來很細緻，不會反光。','收到時有附保護套，很貼心。','顏色跟螢幕上看到的差不多，沒有色差。','送人之前自己先留了一張。','尺寸小巧，不佔位置。','有拿給同事看，大家都想問連結。','現在放在辦公桌上，抬頭就能看到。','細節放大看也沒有糊掉。','整組擺在一起很有秩序感。','朋友收到以後還特地拍照傳給我。','我比較喜歡明信片那一面的構圖。','套組裡的小東西做得比我想的細。')
$posClose=@('會再來逛逛。','推薦給喜歡這類東西的人。','先給五顆星。','下次想收其他系列。','值得收藏。','CP 值不錯。','打算再多買幾份送人。','','','','')
$posArtifact=@('我本來就對「{artifact}」有興趣，收到後又回去翻了一次圖鑑，覺得更有感覺。','「{artifact}」這件我很喜歡，做成明信片之後拿來擺很好看。','因為看了「{artifact}」的介紹才下單，收到的成品沒讓我失望。','「{artifact}」的線條在印刷上處理得很乾淨，我會想一直看下去。','買「{artifact}」這組是想送給朋友，結果自己先捨不得。','「{artifact}」的主圖色調很穩，放在桌上不會太搶眼，我很喜歡。','原本只是隨手點進「{artifact}」的頁面，後來忍不住下單，收到後覺得值得。','我很少買紀念品，但「{artifact}」這組真的做得不錯。','我先在遊戲裡認識「{artifact}」，現在實體拿在手上有種很特別的感覺。','「{artifact}」這組放在書桌旁，每天都會多看它一眼。')
$neuOpen=@('整體還可以，沒有特別驚喜也沒有踩雷。','中規中矩，跟商品頁寫的差不多。','東西本身沒問題，只是我期待可能放太高。','拿到手覺得普通，但價格可以接受。','明信片不錯，另一個小物就比較普通。','算是剛好的紀念品，不會想特別推薦。','顏色有一點點偏深，其他都還行。','包裝簡單但完整。','不算差，也沒有到驚豔。','比我想的小一點，不過商品頁有寫尺寸，是我沒看仔細。')
$neuDetail=@('給喜歡的人應該會很滿意。','如果是送禮的話可以考慮。','放著當收藏還行。','我自己用起來沒什麼意見。','下次可能會選別的款式。','等有折扣再買會比較划算。','')
$neuArtifact=@('「{artifact}」這組算是中規中矩，主圖好看，其他部分沒有特別亮點。','我是衝著「{artifact}」買的，成品看起來還行，只是沒有想像中震撼。','「{artifact}」的明信片比複製品好看，這點跟我預期的不太一樣。')
$lowShip=@('商品沒問題，但物流多等了好幾天。','東西是好的，配送通知太晚才來。','外盒有一角壓到，裡面還好沒壞。','等了比預期久，收到時已經快忘記買過了。','包裹到的時候紙箱有點濕，商品還是完整的。','出貨比預期慢，客服回覆也有點慢。','配送員放在管理室，我隔了兩天才發現。','外箱破了一個小角，內容物沒事，只是心情有點差。','寄送時間拉得比較長，下次會提早買。')
$lowSubj=@('是我自己想像得太大了，收到後有點落差。','介紹有寫尺寸，但我還是以為會更大。','家人很喜歡，我自己覺得不是我的風格。','看照片覺得很不錯，實物色調比我想的暗。','拿到後覺得平平，可能是我期待太高。','紙張比我預期的薄一點，其他都還可以。','單看沒什麼問題，只是跟價格相比我覺得普通。','顏色跟螢幕上看起來有一點差距。')
$lowClose=@('商品本身倒是沒問題。','希望之後配送可以快一點。','還好裡面沒壞。','','','')

function Get-ReviewText([int]$Rating,[string]$Artifact) {
    $hasArtifact=-not [string]::IsNullOrWhiteSpace($Artifact)
    if($Rating -ge 4){
        if($hasArtifact -and $random.Next(100) -lt 30){ $text=(Pick $posArtifact).Replace('{artifact}',$Artifact.Trim()); if($random.Next(3) -eq 0){$text+=Pick $posClose}; return $text }
        $text=(Pick $posOpen)
        if($random.Next(100) -lt 70){$text+=Pick $posDetail}
        $text+=Pick $posClose
        return $text
    }
    if($Rating -eq 3){
        if($hasArtifact -and $random.Next(100) -lt 25){ return (Pick $neuArtifact).Replace('{artifact}',$Artifact.Trim()) }
        return (Pick $neuOpen)+(Pick $neuDetail)
    }
    if($random.Next(5) -lt 3){ return (Pick $lowShip)+(Pick $lowClose) }
    return (Pick $lowSubj)+(Pick $lowClose)
}

$byProduct=$reviews | Group-Object ProductId
$rows=[Collections.Generic.List[string]]::new()
$skipped=0
foreach($group in $byProduct){
    $members=@($group.Group)
    if($members.Count -gt $users.Count){ $skipped+=$members.Count-$users.Count; $members=$members[0..($users.Count-1)] }
    $shuffled=@($users | Sort-Object { $random.Next() })
    for($i=0;$i -lt $members.Count;$i++){
        $r=$members[$i]
        $text=(Get-ReviewText ([int]$r.Rating) $r.Artifact).Replace("'","''")
        if($text.Contains([string][char]0xFF1B)){throw 'Semicolon found in review text.'}
        $rows.Add("('$($r.Id)','$($shuffled[$i].Id)',N'$text')")
    }
}
if($skipped -gt 0){throw "Product has more reviews than accounts: $skipped"}

# 先搬到暫存表再一次更新，避免 (ProductId, UserId) 唯一索引在逐筆更新時暫時衝突。
$sql=[Text.StringBuilder]::new('SET XACT_ABORT ON; BEGIN TRANSACTION;' + "`n")
[void]$sql.AppendLine('CREATE TABLE #R (Id uniqueidentifier PRIMARY KEY, UserId uniqueidentifier NOT NULL, Content nvarchar(1000) NOT NULL);')
for($offset=0;$offset -lt $rows.Count;$offset+=500){
    $last=[Math]::Min($offset+499,$rows.Count-1)
    [void]$sql.AppendLine('INSERT INTO #R (Id,UserId,Content) VALUES')
    [void]$sql.AppendLine(($rows[$offset..$last] -join ",`n")+';')
}
[void]$sql.AppendLine('UPDATE r SET r.UserId=t.UserId, r.Content=t.Content FROM store.ProductReviews r JOIN #R t ON t.Id=r.Id;')
[void]$sql.AppendLine('IF @@ROWCOUNT <> ' + $rows.Count + ' THROW 50001, N''Review update count mismatch'', 1;')
[void]$sql.AppendLine('COMMIT;')
$path=Join-Path ([IO.Path]::GetTempPath()) 'qmah-review-refresh-v1.0.2.sql'
[IO.File]::WriteAllText($path,$sql.ToString(),[Text.UTF8Encoding]::new($true))
try {
    & sqlcmd -S $ServerInstance -E -C -b -d $Database -f 65001 -i $path | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Review refresh failed.'}
    Write-Host "REVIEWS_REFRESHED=$($rows.Count); ACCOUNTS=$($users.Count); SEED=$Seed"
} finally { if($SqlOutPath){Copy-Item -LiteralPath $path -Destination $SqlOutPath -Force}; Remove-Item -LiteralPath $path -Force }
