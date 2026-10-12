"""db-v1.0.3：把社群貼文與留言裡的分號改掉，並把罐頭式留言換成自然的短回覆。
洗版測試用的貼文（標題見 SPAM_TITLES）一律不動。輸出 UPDATE 腳本，由呼叫端套用到本機與遠端。"""
import random, re, subprocess, sys

SERVER, DB, SEED = '.', 'QMAH', 173103
SPAM_TITLES = ('一直發一直發', '低價出清', '幫我衝人氣', '限時搶購', '123')

def query(sql):
    out = subprocess.run(['sqlcmd', '-S', SERVER, '-E', '-C', '-d', DB, '-y', '0', '-f', '65001', '-Q', 'SET NOCOUNT ON; ' + sql],
                         capture_output=True, check=True).stdout.decode('utf-8', 'replace')
    return [l.split('\x01') for l in out.splitlines() if '\x01' in l]

def fix_post(text):
    text = re.sub(r'文中提到的作品可以回到圖鑑(.+?)核對；分類是(.+?)，年代資料則保留(.+?)的原始範圍', r'文中提到的作品是\1，我回圖鑑確認過，分類是\2，年代記載為\3', text)
    text = re.sub(r'這次參觀地點記為(.+?)；地圖適合確認相對位置，入口、樓層和交通仍以文字說明為準', r'這次參觀的地點是\1。地圖只適合看相對位置，入口、樓層和交通還是以文字說明為準', text)
    text = re.sub(r'優惠券取得後，可在結帳時套用於(.+?)；有效天數為 (\d+) 天', r'優惠券取得後，可在結帳時用在\1，有效期限是 \2 天', text)
    lines = []
    for line in text.split('\n'):
        official = '原始說明' in line or '目前圖鑑記載' in line
        lines.append(line.replace('；', '，' if official else '。').replace(';', '，'))
    return re.sub(r'。。+', '。', '\n'.join(lines))

GENERIC = ['你把你把']  # placeholder, replaced below
GENERIC_RE = re.compile('|'.join([
    r'^你把.*寫成具體的觀看經過', r'^我下次遇到相近的作品或情況', r'^如果能再補一個可以回查的來源', r'^如果要繼續查證，我會先找同類',
    r'^我喜歡這篇保留不確定性', r'^我剛好在另一件相近作品', r'^這個角度也很適合拿來做遊戲題目', r'^這個整理方式很清楚，尤其',
    r'^補充一個小想法', r'^這篇把一個具體的觀看細節', r'^提示先讓人看清輪廓']))
ANNOUNCE_RE = re.compile('|'.join([r'^這則公告把資料整理', r'^讀完公告後', r'^活動資訊整理得很完整', r'^這種原則也能幫忙檢查展示資料', r'^先把操作和時間說清楚']))
POOL_GENERIC = [
 '我也有類似的經驗，後來都會順手把拍攝角度記下來。', '這個觀察很有意思，下次去看展我也會特別留意。', '謝謝分享，這個細節我之前完全沒注意到。',
 '看完想回去把自己的筆記重新整理一次。', '請問這是在哪個展廳看到的？我想找時間去看看。', '我跟你的感覺很像，第一眼先看到的反而不是最重要的地方。',
 '寫得很誠實，沒有硬下結論，我反而更想繼續看下去。', '這個比較方式不錯，我下次也來試試。', '我之前把兩件很像的搞混過，這篇剛好提醒我。',
 '照片拍得很清楚，光線也處理得很好。', '原來還有這種看法，學到了。', '下次有新發現記得分享，我會追蹤。', '我最近也在整理同一類的作品，很有共鳴。',
 '這件我也很喜歡，尺寸比我想像的小。', '感謝補充，我回去再查一下來源。', '你的順序很實用，我下次照著看一遍。', '看到這篇才想起自己拍過類似的角度，等等去翻照片。',
 '這樣記錄很清楚，之後回頭看也不會忘記當時的想法。', '我也是看了器底才改變判斷的，真的差很多。', '寫得很有畫面，好像跟著你走了一圈。',
 '先收藏起來，之後去現場時對照著看。', '這個問題我也想知道答案，期待有人補充。', '我覺得最有用的是你把不確定的地方也寫出來。', '看完去圖鑑搜了一下，真的跟你說的一樣。',
 '同意，光看顏色很容易誤會，形狀跟比例也要一起看。', '很實用的小技巧，我也會這樣整理。', '我上次去也有類似的感覺，人少的時候真的看得比較仔細。',
 '這個角度我沒想過，謝謝你的分享。', '有照片的話更好懂，下次可以多放幾張。', '剛好最近也想去這個展，這篇幫了大忙。', '我的筆記也是分成兩欄，這樣真的比較不會亂。',
 '很喜歡你的寫法，輕鬆又不會漏掉重點。', '補一個小經驗：我會把當時的燈光也寫進去，之後比較好對照。', '好奇你後來有沒有找到更確切的資料？',
 '哈哈我也常常這樣，看完才發現漏了最重要的地方。', '收穫很多，謝謝。', '這篇讓我想重新看一次那件作品。']
POOL_ANNOUNCE = [
 '公告寫得很清楚，來源跟推測分開放，之後查起來比較安心。', '看完公告，我會先去確認活動時間跟地點。', '資訊很完整，名額跟報名狀態我再去活動頁看一下。',
 '謝謝整理，這樣新手也看得懂流程。', '我比較在意時間的部分，先記下來了。', '公告的重點一眼就找得到，很好讀。', '這個規則寫出來很有幫助，不然我常常忘記要先確認哪些事。',
 '希望之後有更新也能在公告裡看到。', '我已經把日期記進行事曆了。', '條件寫得很明白，不用再另外問。', '看到這個消息很開心，期待活動當天。', '流程簡單明瞭，我會準時參加。']

def fix_comment(cid, text, rng):
    if GENERIC_RE.search(text): return rng.choice(POOL_GENERIC)
    if ANNOUNCE_RE.search(text): return rng.choice(POOL_ANNOUNCE)
    return text.replace('；', '。').replace(';', '，')

def lit(s): return "N'" + s.replace("'", "''") + "'"

def main(out):
    rng = random.Random(SEED)
    posts = query("SELECT CONVERT(varchar(36),Id)+CHAR(1)+REPLACE(REPLACE(Content,CHAR(13),N''),CHAR(10),CHAR(2))+CHAR(1)+REPLACE(Title,CHAR(10),N' ') FROM social.SocialPosts ORDER BY Id")
    comments = query("SELECT CONVERT(varchar(36),Id)+CHAR(1)+REPLACE(REPLACE(Content,CHAR(13),N''),CHAR(10),CHAR(2)) FROM social.SocialComments ORDER BY Id")
    sql = ['SET XACT_ABORT ON; BEGIN TRANSACTION;']
    n_post = n_comment = 0
    for pid, content, title in posts:
        if title.startswith(SPAM_TITLES) or ('；' not in content and ';' not in content): continue
        new = fix_post(content.replace('\x02', '\n'))
        if new != content.replace('\x02', '\n'):
            sql.append(f"UPDATE social.SocialPosts SET Content={lit(new).replace(chr(10), chr(39)+'+NCHAR(10)+N'+chr(39))} WHERE Id='{pid}';"); n_post += 1
    for cid, content in comments:
        new = fix_comment(cid, content.replace('\x02', '\n'), rng)
        if new != content.replace('\x02', '\n'):
            sql.append(f"UPDATE social.SocialComments SET Content={lit(new).replace(chr(10), chr(39)+'+NCHAR(10)+N'+chr(39))} WHERE Id='{cid}';"); n_comment += 1
    sql.append('COMMIT;')
    open(out, 'w', encoding='utf-8-sig').write('\n'.join(sql))
    print(f'POSTS={n_post} COMMENTS={n_comment}')

if __name__ == '__main__':
    main(sys.argv[1])
