[CmdletBinding()]
param([string]$ServerInstance='.', [string]$Database='QMAH', [int]$Seed=173101, [switch]$RefreshGeneratedContent)
$ErrorActionPreference='Stop'
function Read-JsonRows([string]$Query) {
    $result=& sqlcmd -S $ServerInstance -E -C -b -d $Database -y 0 -w 65535 -Q "SET NOCOUNT ON; $Query FOR JSON PATH;"
    if($LASTEXITCODE -ne 0){throw 'Could not read seed input.'}
    $json=($result | ForEach-Object {$_.Trim()}) -join ''
    return $json.Substring($json.IndexOf('[')) | ConvertFrom-Json
}
$products=@(Read-JsonRows 'SELECT Id,Name FROM store.Products ORDER BY Id')
$users=@(Read-JsonRows "SELECT u.Id FROM [user].AspNetUsers u WHERE u.Status=N'ACTIVE' AND NOT EXISTS (SELECT 1 FROM [user].AspNetUserRoles ur JOIN [user].AspNetRoles r ON r.Id=ur.RoleId WHERE ur.UserId=u.Id AND r.Name=N'Admin') ORDER BY u.Id")
$existing=@(Read-JsonRows 'SELECT Id,ProductId,UserId,Rating,Content FROM store.ProductReviews')
if($users.Count -lt 7){throw 'At least seven demo members are required.'}
$pairs=[Collections.Generic.HashSet[string]]::new()
$texts=[Collections.Generic.HashSet[string]]::new()
foreach($review in $existing){[void]$pairs.Add("$($review.ProductId)|$($review.UserId)".ToLowerInvariant());[void]$texts.Add($review.Content)}
$shipping=@('商品沒問題，但物流多等了幾天。','東西是好的，配送通知太晚才來。','對商品沒有意見，只是不喜歡等包裹。','外盒有一角壓到，裡面倒是完整。','貨運送到時我不在，又多跑了一趟。','急著拿來送人，配送比我期待的慢。','包裹狀態好幾天沒更新，看得很煩。','配送時間跟我的作息不合。','原本期待週末前收到，結果晚了。','外包裝看起來有點狼狽，商品沒有壞。','領件通知來得突然，差點錯過。','地址沒錯，配送卻繞了一下才到。')
$subjective=@('是我自己想像得太大了，收到後有點落差。','介紹有寫尺寸，但我還是以為會更大。','家人很喜歡，我自己覺得不是我的風格。','看照片覺得很心動，實物和房間不太搭。','東西跟描述差不多，只是沒有我腦補的驚喜。','本來想要原作那種氣勢，小複製品當然不一樣。')
$random=[Random]::new($Seed)
$positive=@('商品很滿意，跟照片差不多。','收到很喜歡，符合預期。','實物很好看，沒有買錯。','包裝完整，東西也不錯。','質感比我想的好，滿意。','送朋友的，對方很喜歡。','明信片印得很清楚。','擺在書桌上剛剛好。','這個價格我覺得可以。','買來收藏的，收到沒有失望。','尺寸跟介紹一樣，沒有落差。','顏色很漂亮，越看越喜歡。','第二次買了，這次也滿意。','收到的東西跟描述相符。','本來有點猶豫，拿到覺得值得。','很好看，想再挑別款。','商品完整，沒有破損。','喜歡！照片沒有拍出實物的感覺。','複製品跟明信片一起看滿有意思。','家人說好看，買得值得。')
$details=@('包裝也有顧到。','大小是我想要的。','顏色沒有明顯色差。','實物比照片好看一點。','送禮不用再另外搭東西。','已經放在書架上了。','明信片先留著捨不得寄。','小朋友也覺得好看。','下次再買別款。','收到後有仔細看，沒什麼問題。','比我原本想像的精緻。','放在家裡滿搭的。')
$neutral=@('商品還可以，沒有特別驚喜。','跟描述差不多，就一般紀念品。','明信片比較喜歡，另一件普通。','收到完整，暫時沒什麼問題。','顏色不錯，但沒有想像中喜歡。','這個價錢算可以，沒有期待太多。','整體還行，之後不一定會再買。','尺寸要先看清楚，比照片感覺小。')
function Get-ReviewText([int]$Rating) {
    if($Rating -ge 4){
        $text=$positive[$random.Next($positive.Count)]
        if($random.Next(3) -ne 0){$text+=$details[$random.Next($details.Count)]}
    } elseif($Rating -eq 3){
        $text=$neutral[$random.Next($neutral.Count)]
        if($random.Next(4) -eq 0){$text+='商品本身沒有壞。'}
    } else {
        if($random.Next(5) -lt 4){
            $text=$shipping[$random.Next($shipping.Count)]
            if($random.Next(2) -eq 0){$text+=@('還好裡面沒壞。','下次會提早一點買。','商品本身倒是沒問題。','希望配送可以快一點。')[$random.Next(4)]}
        } else {
            $text=$subjective[$random.Next($subjective.Count)]
        }
    }
    return $text
}
if($RefreshGeneratedContent){
    $updates=[Text.StringBuilder]::new('SET XACT_ABORT ON; BEGIN TRANSACTION;' + "`n")
    $updated=0
    foreach($review in $existing){
        $pair="$($review.ProductId)|$($review.UserId)".ToLowerInvariant()
        $digest=[Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("demo-review-v1.0.1|$Seed|$pair"))
        $id=[Guid]::new([byte[]]$digest[0..15])
        if($id -ne [Guid]$review.Id){continue}
        $content=(Get-ReviewText $review.Rating).Replace("'","''")
        [void]$updates.AppendLine("UPDATE store.ProductReviews SET Content=N'$content' WHERE Id='$id';")
        $updated++
    }
    [void]$updates.AppendLine('COMMIT;')
    $refreshPath=Join-Path ([IO.Path]::GetTempPath()) 'qmah-demo-review-refresh-v1.0.1.sql'
    [IO.File]::WriteAllText($refreshPath,$updates.ToString(),[Text.UTF8Encoding]::new($true))
    try {
        & sqlcmd -S $ServerInstance -E -C -b -d $Database -f 65001 -i $refreshPath | Out-Null
        if($LASTEXITCODE -ne 0){throw 'Review refresh failed.'}
        Write-Host "DEMO_REVIEW_TEXTS_UPDATED=$updated"
    } finally { Remove-Item -LiteralPath $refreshPath -Force }
    exit 0
}
$values=[Collections.Generic.List[string]]::new()
$baseDate=[DateTime]::SpecifyKind([DateTime]'2026-10-11T16:00:00',[DateTimeKind]::Utc)
foreach($product in $products){
    $candidates=@($users | Where-Object {-not $pairs.Contains("$($product.Id)|$($_.Id)".ToLowerInvariant())} | Sort-Object {$random.Next()})
    $count=[Math]::Min($random.Next(3,7),$candidates.Count)
    for($i=0;$i -lt $count;$i++){
        $rating=if($i -eq 0){$random.Next(4,6)}elseif($i -eq 1){$random.Next(1,3)}else{ @([byte]3,[byte]4,[byte]4,[byte]5,[byte]5)[$random.Next(5)] }
        $content=Get-ReviewText $rating
        $user=$candidates[$i]
        $pair="$($product.Id)|$($user.Id)".ToLowerInvariant()
        [void]$pairs.Add($pair)
        $digest=[Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("demo-review-v1.0.1|$Seed|$pair"))
        $id=[Guid]::new([byte[]]$digest[0..15])
        $date=$baseDate.AddDays(-$random.Next(2,62)).AddMinutes(-$random.Next(1440)).ToString('yyyy-MM-ddTHH:mm:ss')
        $escaped=$content.Replace("'","''")
        $values.Add("('$id','$($product.Id)','$($user.Id)',$rating,N'$escaped',N'PUBLISHED','$date','$date')")
    }
}
$sql=[Text.StringBuilder]::new('SET XACT_ABORT ON; BEGIN TRANSACTION;' + "`n")
for($offset=0;$offset -lt $values.Count;$offset+=500){
    [void]$sql.AppendLine('INSERT INTO store.ProductReviews (Id,ProductId,UserId,Rating,Content,Status,CreatedAt,UpdatedAt) VALUES')
    $last=[Math]::Min($offset+499,$values.Count-1)
    [void]$sql.AppendLine(($values[$offset..$last] -join ",`n")+';')
}
[void]$sql.AppendLine('COMMIT;')
$output=Join-Path ([IO.Path]::GetTempPath()) 'qmah-demo-review-seed-v1.0.1.sql'
[IO.File]::WriteAllText($output,$sql.ToString(),[Text.UTF8Encoding]::new($true))
try {
    & sqlcmd -S $ServerInstance -E -C -b -d $Database -f 65001 -i $output
    if($LASTEXITCODE -ne 0){throw 'Product review seeding failed.'}
    Write-Host "DEMO_REVIEWS_ADDED=$($values.Count); PRODUCTS=$($products.Count); SEED=$Seed"
} finally { Remove-Item -LiteralPath $output -Force }
