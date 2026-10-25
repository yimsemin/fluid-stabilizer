"""README 비교 GIF·스크린샷 조립. 저장소 루트에서: python/.venv/bin/python docs/images/make_compare_gif.py
(먼저 SceneCapture.GrabCompareFrames 로 Logs/gif/ 프레임을 만든다 — docs/reproduce-from-scratch.md 4절.)"""
import csv,sys
from PIL import Image, ImageDraw, ImageFont
src='Logs/gif'; out='docs/images'
rows=list(csv.DictReader(open(f'{src}/spill.csv')))
try: font=ImageFont.truetype('/System/Library/Fonts/AppleSDGothicNeo.ttc',22); small=ImageFont.truetype('/System/Library/Fonts/AppleSDGothicNeo.ttc',16)
except Exception as e: print(e); font=small=ImageFont.load_default()
W,H=400,225
def frame(i):
    r=rows[i]; a=Image.open(f'{src}/f{i:03d}-0.png').resize((W,H)); b=Image.open(f'{src}/f{i:03d}-1.png').resize((W,H))
    img=Image.new('RGB',(W,H*2+36),(24,24,28)); img.paste(a,(0,36)); img.paste(b,(0,36+H))
    d=ImageDraw.Draw(img)
    d.text((10,6),f"시간 {float(r['t']):4.1f} s (4배속) · 같은 주행, 같은 도로",font=small,fill=(235,235,235))
    for k,(lab,key,y) in enumerate([("① 강체 (아무것도 안 함)",'spill1',36),("② 규칙기반 (PID)",'spill2',36+H)]):
        d.rectangle((0,y+H-34,W,y+H),fill=(0,0,0,))
        d.text((10,y+H-30),f"{lab}   누적 스필 {float(r[key]):5.1f} L",font=small,fill=(255,255,255))
    return img
frames=[frame(i) for i in range(0,len(rows),2)]
pal=[f.quantize(colors=48,method=Image.Quantize.MEDIANCUT,dither=Image.Dither.NONE) for f in frames]
pal[0].save(f'{out}/demo-compare.gif',save_all=True,append_images=pal[1:]+[pal[-1]]*8,duration=200,loop=0,optimize=True)
frame(len(rows)*3//4).save(f'{out}/screenshot-compare.png',optimize=True)
