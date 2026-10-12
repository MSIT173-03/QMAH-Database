"""db-v1.0.5：商城商品說明不再含分號。套組與提示句型改成句號，引用的館藏原文改成逗號。輸出 UPDATE 腳本。"""
import importlib.util, re, sys
spec = importlib.util.spec_from_file_location('st', __file__.replace('Refresh-ProductSemicolons', 'Refresh-SocialText'))
st = importlib.util.module_from_spec(spec); spec.loader.exec_module(st)
TEMPLATE = re.compile(r'明信片|縮小複製品|^如果|本套組|^\d+\. |值得回查|適合先|主圖|閱讀時')

def fix(text):
    out = []
    for line in text.split('\n'):
        out.append(line.replace('；', '。' if TEMPLATE.search(line) else '，').replace(';', '，'))
    return re.sub(r'。。+', '。', '\n'.join(out))

rows = st.query("SELECT CONVERT(varchar(36),Id)+CHAR(1)+REPLACE(REPLACE(Description,CHAR(13),N''),CHAR(10),CHAR(2)) FROM store.Products WHERE Description LIKE N'%；%' OR Description LIKE N'%;%'")
sql = ['SET XACT_ABORT ON; BEGIN TRANSACTION;']
for pid, d in rows:
    old = d.replace('\x02', '\n'); new = fix(old)
    sql.append(f"UPDATE store.Products SET Description={st.lit(new).replace(chr(10), chr(39)+'+NCHAR(10)+N'+chr(39))} WHERE Id='{pid}';")
sql.append('COMMIT;')
open(sys.argv[1], 'w', encoding='utf-8-sig').write('\n'.join(sql)); print('PRODUCTS', len(rows))
