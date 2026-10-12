"""db-v1.0.4：把產生器寫的社群貼文正文（四種固定句型的作品整理文與活動介紹）改寫成自然的第一人稱筆記。
作品資料（分類、年代、尺寸、原始說明）原樣保留，只改框架句。洗版測試、67、公告類貼文不動。輸出 UPDATE 腳本。"""
import importlib.util, random, re, sys
spec = importlib.util.spec_from_file_location('st', __file__.replace('Refresh-SocialPosts', 'Refresh-SocialText'))
st = importlib.util.module_from_spec(spec); spec.loader.exec_module(st)
SEED = 173104
SKIP_TITLES = st.SPAM_TITLES + ('676767', '6767')

FAMILIES = [
 re.compile(r'^這次整理(?P<a>.+?)時，我先從(?P<f>.+?)切入，.*?\n\n目前圖鑑記載：分類(?P<c>.+?)、年代(?P<e>.+?)、尺寸(?P<s>.+?)。原始說明提到：(?P<d>.*?)\n\n我把這篇', re.S),
 re.compile(r'^整理(?P<a>.+?)時，我先把目前能直接.*?\n\n目前資料記載：分類為(?P<c>.+?)，年代原文為(?P<e>.+?)，尺寸為(?P<s>.+?)。原始說明提到：(?P<d>.*?)\n\n我想請大家一起看的，是(?P<f>.+?)這個角度', re.S),
 re.compile(r'^第一次看到(?P<a>.+?)時，我的注意力.*?\n\n圖鑑資料顯示它屬於(?P<c>.+?)，年代原文為(?P<e>.+?)，尺寸是(?P<s>.+?)。原始說明中可以直接回查的內容是：(?P<d>.*?)\n\n如果要把這件', re.S),
 re.compile(r'^我把(?P<a>.+?)的整理分成三層.*?\n\n這件文物目前的分類是(?P<c>.+?)，年代欄位保留為(?P<e>.+?)，尺寸欄位為(?P<s>.+?)。原始說明摘要如下：(?P<d>.*?)\n\n之後如果', re.S),
]
EVENT = re.compile(r'^這篇貼文介紹(?P<k>會員|官方)活動(?P<t>.+?)。活動時間是 (?P<time>.+?)，地點為 (?P<loc>.+?)。正式時間、名額與報名狀態，請以活動頁最新資訊為準。\n\n(?P<mid>.*?)\n\n如果想參加，可以先確認活動日期、到場位置與報名截止日。活動結束後也歡迎回來分享現場看到的作品與問題。$', re.S)

OPEN_F = ['最近翻圖鑑翻到{a}，這次我想先從{f}看起。', '{a}我看了好幾回，這次決定先看{f}，其他欄位放到後面再對。',
          '這次整理{a}，我從{f}下手，因為只盯一個紋樣或顏色，很容易把局部當成全部。', '先看{f}，再回頭對照整張影像，這是我整理{a}的順序。',
          '我整理{a}時第一個查的是{f}，後面的尺寸和來源才有東西可以對照。']
OPEN_NOF = ['{a}這件我看了不只一次，這次想慢慢記下來。', '圖鑑裡的{a}，我這回認真讀了一遍欄位。', '這次輪到{a}，先把圖鑑上能確認的東西寫下來。',
            '我最近在看{a}，邊看邊記，順手整理成這篇。', '{a}的照片我先看完整輪廓，再去翻文字資料。']
FACT = ['圖鑑分類是{c}，年代記為{e}，尺寸{s}。', '圖鑑上它屬於{c}，年代寫{e}，尺寸{s}。', '分類{c}，年代{e}，尺寸{s}，這是圖鑑目前的紀錄。']
SIZE_NA = ['尺寸還沒有測量資料', '尺寸欄位還是待測量']
DESC = ['原始說明是這樣寫的：{d}', '館方的說明原文：{d}', '說明原文提到：{d}', '我把原始說明貼在這裡：{d}']
CLOSE_F = ['{f}這一塊我還沒有把握，有不同看法的話，請直接指出影像或欄位的位置。', '關於{f}，我最想聽大家怎麼看，有出處的話更好。',
           '{f}這個角度我先寫到這裡，之後找到更多資料會再補。']
CLOSE = ['我先把這些記下來，之後找到新資料再回來改。', '還有幾個地方沒查清楚，我留著不下結論。', '如果你手上有相近的作品或別的角度的照片，歡迎貼上來一起比。',
         '這篇算是筆記，有錯的地方請直接留言告訴我。', '目前只能說到這裡，其餘的等有來源再說。', '寫完回頭看，資料和我的感覺是分開的，我想一直保持這樣。']
EV_OPEN = ['「{t}」是{k}辦的活動，{time} 開始，地點在{loc}。名額和報名狀況以活動頁為準。', '{k}活動「{t}」訂在 {time}，地點是{loc}。報名狀態請看活動頁。']
EV_CLOSE = ['想參加的話，先確認日期、地點和報名截止日。活動結束後也歡迎回來聊聊看到的作品。', '有興趣的話記得先看報名截止日。結束後歡迎回來分享你看到的作品和問題。']

CANNED = re.compile(r'^(讀完後我會想再找不同角度的影像|我也會先看.+?的完整輪廓，再回頭確認這篇提到的細節|如果要比較同類作品，我會再加上器底)')

def rewrite(ct, title, rng):
    for i, rx in enumerate(FAMILIES):
        m = rx.match(ct)
        if not m: continue
        g = m.groupdict(); f = g.get('f')
        if f is None and i == 2:  # 第一次看到：沒有焦點欄位
            f = None
        o = rng.choice(OPEN_F).format(a=g['a'], f=f) if f and i in (0, 1) else rng.choice(OPEN_NOF).format(a=g['a'])
        s = rng.choice(SIZE_NA) if '待測量' in g['s'] else '是' + g['s'] if not g['s'].startswith('是') else g['s']
        fact = rng.choice(FACT).format(c=g['c'], e=g['e'], s=s).replace('尺寸是尺寸', '尺寸')
        d = rng.choice(DESC).format(d=g['d'].strip())
        c = rng.choice(CLOSE_F).format(f=f) if f and i == 1 else rng.choice(CLOSE)
        return f'{o}\n\n{fact}{d}\n\n{c}'.replace('尺寸是待測量', '尺寸待測量')
    m = EVENT.match(ct)
    if m:
        g = m.groupdict()
        return f"{rng.choice(EV_OPEN).format(**g)}\n\n{g['mid']}\n\n{rng.choice(EV_CLOSE)}"
    return None

def main(out):
    rng = random.Random(SEED)
    posts = st.query("SELECT CONVERT(varchar(36),Id)+CHAR(1)+REPLACE(REPLACE(Content,CHAR(13),N''),CHAR(10),CHAR(2))+CHAR(1)+REPLACE(Title,CHAR(10),N' ') FROM social.SocialPosts ORDER BY Id")
    sql = ['SET XACT_ABORT ON; BEGIN TRANSACTION;']; n = miss = 0
    for pid, content, title in posts:
        if title.startswith(SKIP_TITLES): continue
        ct = content.replace('\x02', '\n'); new = rewrite(ct, title, rng)
        if new is None or new == ct: continue
        assert '；' not in new and ';' not in new, pid
        sql.append(f"UPDATE social.SocialPosts SET Content={st.lit(new).replace(chr(10), chr(39)+'+NCHAR(10)+N'+chr(39))} WHERE Id='{pid}';"); n += 1
    cm = st.query("SELECT CONVERT(varchar(36),Id)+CHAR(1)+REPLACE(REPLACE(Content,CHAR(13),N''),CHAR(10),CHAR(2)) FROM social.SocialComments ORDER BY Id")
    for cid, content in cm:
        if CANNED.match(content):
            sql.append(f"UPDATE social.SocialComments SET Content={st.lit(rng.choice(st.POOL_GENERIC))} WHERE Id='{cid}';")
    sql.append('COMMIT;')
    open(out, 'w', encoding='utf-8-sig').write('\n'.join(sql)); print(f'POSTS={n}')

if __name__ == '__main__':
    main(sys.argv[1])
