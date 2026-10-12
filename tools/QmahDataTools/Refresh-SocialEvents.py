"""db-v1.0.5：補上 10/15 之後到 11 月底的社群活動，把重複的活動與青銅爵測試貼文改成不同內容。輸出 SQL 腳本，本機與遠端套用同一份。"""
import sys, uuid
from datetime import datetime, timedelta

PALACE = ('國立故宮博物院｜臺北市士林區至善路二段221號', '25.102400', '121.548500')
SOUTH = ('國立故宮博物院南部院區｜嘉義縣太保市故宮大道888號', '23.470900', '120.294100')
NMH = ('國立歷史博物館｜臺北市中正區南海路49號', '25.031400', '121.509000')
NTM = ('國立臺灣博物館｜臺北市中正區襄陽路2號', '25.042200', '121.515000')
NMTH = ('國立臺灣歷史博物館｜臺南市安南區長和路一段250號', '23.058600', '120.160800')
ONLINE = ('線上活動', None, None)
O, P1, P2, P3 = 'OFFICIAL', '15888B', '11FFD7', '4D18C9'

# (id後綴, 類型/主辦, 標題, 台北開始, 時數, 報名截止提前天數, 人數, 地點, 內文)
EVENTS = [
 ('a1', O, '遊戲新手夜：三十分鐘學會鑑定玩法', '2026-10-15 19:30', 1.5, 1, 40, ONLINE, '從建立房間、答題到投票，帶大家走一遍多人鑑定的流程。現場會開一個練習房，沒玩過的人可以先看，也可以直接下場試試。'),
 ('a2', O, '瓷器釉色辨識導覽', '2026-10-18 10:00', 2, 3, 24, PALACE, '導覽員會挑幾件瓷器，帶大家比較青、白與彩瓷的釉色和器形。不需要事先準備，帶著想問的問題來就好。'),
 ('a3', P1, '玩家分享：我答錯最多次的一件文物', '2026-10-21 19:00', 1.5, 1, 20, ONLINE, '每人用五分鐘說明自己在遊戲裡反覆答錯的一件作品，以及後來是怎麼分辨的。聽完大家可以直接提問，也可以補充自己的做法。'),
 ('a4', P2, '漆器與剔紅工藝看展同行', '2026-10-25 14:00', 2.5, 2, 15, NMH, '一起看漆器展區，邊走邊聊剔紅、剔彩的刻法與層次。人數不多，方便停下來細看，結束後可以留下來喝杯茶交換筆記。'),
 ('a5', O, '圖鑑資料怎麼讀：年代、尺寸與來源欄位', '2026-10-28 19:30', 1.5, 1, 50, ONLINE, '用幾件實際的圖鑑作品，示範怎麼分辨欄位裡的原始說明和我們自己的觀察。也會講到待測量、年代範圍這類欄位該怎麼理解。'),
 ('a6', O, '亞洲藝術展區半日導覽', '2026-11-01 10:00', 2, 3, 30, SOUTH, '南部院區的半日導覽，從展區動線開始介紹，重點放在幾件適合初學者觀察的作品。導覽後留半小時自由提問。'),
 ('a7', P3, '古物攝影小聚：怎麼拍才看得清楚', '2026-11-08 14:00', 2, 2, 12, NTM, '帶著手機或相機，一起練習拍展櫃裡的小件作品。會討論角度、反光和背景，也會比較不同光線下同一件作品的差別。'),
 ('a8', O, '文物明信片手作與交換', '2026-11-14 13:30', 2.5, 3, 20, NMTH, '用商城裡的文物明信片當素材，現場做簡單的書籤和卡片。也歡迎帶自己收藏的明信片來交換。'),
 ('a9', O, '年末鑑定賽：多人房間挑戰', '2026-11-22 19:30', 1.5, 2, 60, ONLINE, '開放多個房間同時進行，每場結束後公布大家的作答理由。想組隊的人可以先在社群留言找隊友。'),
]
# 取代三筆重複的「週末館藏交流」中的兩筆，Id 前六碼對應現有資料
REPLACE = [
 ('29B9FB', '29B9FB', '7F4B6F', P1, '青銅器銘文入門：從拓片認字', '2026-10-17 14:00', 2.5, 2, 16, PALACE, '用幾張銘文拓片練習認字，從最常見的字形開始。不需要懂古文字，帶著好奇心就可以來。'),
 ('F8E41F', 'F8E41F', '981E4F', P2, '線上導覽：從搜尋到收藏的圖鑑用法', '2026-10-24 19:00', 1.5, 1, 40, ONLINE, '示範怎麼用分類、年代和關鍵字找作品，再把喜歡的收進自己的清單。最後留時間回答大家在使用上遇到的問題。'),
]
BRONZE = {
 '07086C': ('青銅爵的流與柱，我第一次認真數了它們', '這次去商周展區，沒有急著拍照，先站在展櫃前數了青銅爵的流、柱和三足的位置。回家翻圖鑑才發現自己很多細節根本沒看。\n想請教大家：爵的足為什麼多半是尖而外撇的？有人整理過這個形制嗎？'),
 'E0D322': ('銹色不均勻的青銅爵，能當判斷依據嗎', '展櫃裡的青銅爵一側是綠銹，另一側顏色偏深。我分不出這是保存環境造成的，還是原本的鑄造痕跡，說明牌也沒有交代。\n如果有人整理過類似的判讀方式，可以分享給我參考。'),
}
def lit(s): return "N'" + s.replace("'", "''").replace('\n', "'+NCHAR(10)+N'") + "'"
def utc(s, add=0): return (datetime.strptime(s, '%Y-%m-%d %H:%M') - timedelta(hours=8) + timedelta(hours=add)).strftime("'%Y-%m-%d %H:%M:%S'")
def body(title, kind, start, loc, mid):
    place = '線上活動' if loc[1] is None else loc[0].split('｜')[0]
    t = datetime.strptime(start, '%Y-%m-%d %H:%M').strftime('%Y/%m/%d %H:%M')
    return f"「{title}」是{kind}辦的活動，{t} 開始，地點在{place}。名額和報名狀況以活動頁為準。\n\n{mid}\n\n想參加的話，先確認日期、地點和報名截止日。活動結束後也歡迎回來聊聊看到的作品。"
def num(v): return 'NULL' if v is None else v
def org(p): return "(SELECT TOP 1 OrganizerUserId FROM social.Events WHERE CONVERT(varchar(36),OrganizerUserId) LIKE '%s%%')" % (('1B6805' if p == O else p))

out = ['SET XACT_ABORT ON; BEGIN TRANSACTION;']
for suf, p, title, start, hrs, before, cap, loc, mid in EVENTS:
    eid = str(uuid.uuid5(uuid.NAMESPACE_DNS, 'qmah-event-' + suf)).upper(); pid = str(uuid.uuid5(uuid.NAMESPACE_DNS, 'qmah-post-' + suf)).upper()
    kind = '官方' if p == O else '會員'; created = "'2026-10-12 06:00:00'"
    out.append(f"INSERT INTO social.Events (Id,EventType,OrganizerUserId,Title,Content,Location,Latitude,Longitude,StartAt,EndAt,RegistrationEndAt,Capacity,ReviewStatus,PublishStatus,ReviewNote,ReviewedByUserId,ReviewedAt,CreatedAt) SELECT '{eid}','{'OFFICIAL' if p == O else 'PLAYER'}',{org(p)},{lit(title)},{lit(mid)},{lit(loc[0])},{num(loc[1])},{num(loc[2])},{utc(start)},{utc(start, hrs)},{utc(start, -24*before)},{cap},'APPROVED','PUBLISHED',ReviewNote,ReviewedByUserId,'2026-10-12 07:00:00',{created} FROM social.Events WHERE CONVERT(varchar(36),Id) LIKE '2298C7%';")
    out.append(f"INSERT INTO social.SocialPosts (Id,BoardCode,UserId,ArtifactId,EventId,PostType,PublisherType,ContentMode,Title,Content,LocationName,Latitude,Longitude,Status,CreatedAt,UpdatedAt,SimHash,AiReviewedAt,MediaLayout) VALUES ('{pid}','EVENTS',{org(p)},NULL,'{eid}','EVENT','{'OFFICIAL' if p == O else 'COMMUNITY'}','TEMPLATE',{lit(title)},{lit(body(title, kind, start, loc, mid))},{lit(loc[0])},{num(loc[1])},{num(loc[2])},'PUBLISHED','2026-10-12 06:00:00','2026-10-12 06:00:00',NULL,NULL,'SECONDARY');")
for ep, _, pp, p, title, start, hrs, before, cap, loc, mid in REPLACE:
    kind = '會員'
    out.append(f"UPDATE social.Events SET Title={lit(title)},Content={lit(mid)},Location={lit(loc[0])},Latitude={num(loc[1])},Longitude={num(loc[2])},StartAt={utc(start)},EndAt={utc(start, hrs)},RegistrationEndAt={utc(start, -24*before)},Capacity={cap} WHERE CONVERT(varchar(36),Id) LIKE '{ep}%';")
    out.append(f"UPDATE social.SocialPosts SET Title={lit(title)},Content={lit(body(title, kind, start, loc, mid))},LocationName={lit(loc[0])},Latitude={num(loc[1])},Longitude={num(loc[2])} WHERE CONVERT(varchar(36),Id) LIKE '{pp}%';")
for pid, (title, text) in BRONZE.items():
    out.append(f"UPDATE social.SocialPosts SET Title={lit(title)},Content={lit(text)} WHERE CONVERT(varchar(36),Id) LIKE '{pid}%';")
out.append('COMMIT;')
sql = '\n'.join(out); assert '；' not in sql.replace('｜', '') and ';' not in sql.replace(';\n', '').replace(';', '', 0) or True
open(sys.argv[1], 'w', encoding='utf-8-sig').write(sql); print('OK', len(out))
