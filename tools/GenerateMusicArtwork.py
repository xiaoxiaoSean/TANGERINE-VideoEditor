"""Create original, deterministic cover art for the packaged music library.

This is an asset-authoring tool, not an application startup dependency. All
shapes are drawn from scratch, without downloaded photographs, stock artwork,
fonts, or album covers. Each recording has a stable visual seed and its actual
genre/mood selects the illustration family. Re-running preserves the art.
"""
from __future__ import annotations

import hashlib
import json
import math
import random
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
LIBRARY = ROOT / "TANGERINE-VideoEditor" / "Resources" / "BuiltinMusic"
WIDTH, HEIGHT, SCALE = 640, 360, 2

# Palettes deliberately differ in both hue and luminance so categories remain
# recognizable in the editor's small media-card view, without relying on text.
PALETTES = {
    "ambient": ("#101C37", "#243B69", "#73DCCE", "#DBF3E6"),
    "electronic": ("#161137", "#33256D", "#F55DCE", "#7DE8F6"),
    "piano": ("#132A34", "#31576A", "#D4B487", "#FFF3DD"),
    "jazz": ("#2D1733", "#603446", "#EAA56F", "#FFE1A8"),
    "acoustic": ("#19372E", "#476646", "#E3B481", "#F8E8C3"),
    "rock": ("#241A31", "#533354", "#F07969", "#F7D69C"),
    "epic": ("#20233D", "#4D4265", "#F4B66B", "#FFF2C7"),
    "dark": ("#121B26", "#253F50", "#C96E81", "#C9E5D6"),
    "romantic": ("#402938", "#895768", "#F5B9A2", "#FFF0D4"),
    "playful": ("#153D51", "#296E81", "#F9C45A", "#F47E73"),
    "world": ("#3A2635", "#765041", "#F0B363", "#BAE0CD"),
    "upbeat": ("#0C3742", "#1F6872", "#EDAD5B", "#FCE8AE"),
    "cinematic": ("#1B2941", "#3F576A", "#F2AA76", "#D9E9DE"),
}


def rgb(value: str) -> tuple[int, int, int]:
    return tuple(int(value[index:index + 2], 16) for index in (1, 3, 5))


def family(track: dict) -> str:
    text = " ".join(str(track.get(key, "")) for key in
                    ("genre", "mood", "sourceCategory", "title", "description")).casefold()
    choices = [
        ("piano", ("piano", "钢琴", "classical", "古典")),
        ("jazz", ("jazz", "爵士", "blues", "蓝调", "swing", "lo-fi", "低保真")),
        ("dark", ("horror", "恐怖", "dark", "悬疑", "mystery", "tense", "紧张")),
        ("epic", ("epic", "史诗", "action", "动作", "battle", "战斗", "hero")),
        ("romantic", ("romantic", "浪漫", "sentimental", "抒情", "sad", "伤感")),
        ("ambient", ("ambient", "氛围", "冥想", "meditat", "relax", "舒缓", "梦幻", "治愈")),
        ("electronic", ("electronic", "电子", "techno", "dance", "舞曲", "synth", "合成器", "chiptune")),
        ("playful", ("comedy", "喜剧", "funny", "搞怪", "playful", "童趣", "children", "儿童", "游戏配乐")),
        ("acoustic", ("acoustic", "原声", "folk", "民谣", "guitar", "吉他", "country", "乡村", "尤克里里")),
        ("rock", ("rock", "摇滚", "metal", "金属")),
        ("world", ("world", "世界", "ethnic", "民族", "afric", "latin", "拉丁", "exotic")),
        ("upbeat", ("upbeat", "欢快", "positive", "阳光", "inspir", "励志", "happy")),
    ]
    # Prefer the declared genre to a coincidental instrument in a song title.
    for source in (str(track.get("genre", "")).casefold(), text):
        match = next((name for name, words in choices if any(word in source for word in words)), None)
        if match:
            return match
    return "cinematic"


def artwork(track: dict) -> tuple[Image.Image, str]:
    seed = int.from_bytes(hashlib.sha256(track["id"].encode()).digest()[:8], "big")
    rng = random.Random(seed)
    theme = family(track)
    base, middle, accent, light = map(rgb, PALETTES[theme])
    field = Image.new("RGB", (WIDTH // 2, HEIGHT // 2))
    pixels = field.load()
    # Render the soft light field with deterministic grain; the thumbnail is
    # already rasterized in the installation, with no runtime rendering work.
    for y in range(field.height):
        for x in range(field.width):
            px, py = x / field.width, y / field.height
            glow = max(0, 1 - math.hypot((px - .63) * 1.05, (py - .37) * .85)) ** 2
            grain = rng.choice((-2, -1, 0, 0, 0, 1, 2))
            pixels[x, y] = tuple(max(0, min(255, int(base[c] * (1 - glow) + middle[c] * glow) + grain)) for c in range(3))
    canvas = field.resize((WIDTH * SCALE, HEIGHT * SCALE), Image.Resampling.BICUBIC)
    layer = Image.new("RGBA", canvas.size)
    draw = ImageDraw.Draw(layer)

    def line(points, color=light, width=2, alpha=220):
        draw.line([(int(x * SCALE), int(y * SCALE)) for x, y in points], fill=(*color, alpha), width=max(1, round(width * SCALE)), joint="curve")

    def ellipse(box, color=accent, alpha=200, outline=False, width=2):
        coordinates = tuple(int(v * SCALE) for v in box)
        if outline:
            draw.ellipse(coordinates, outline=(*color, alpha), width=round(width * SCALE))
        else:
            draw.ellipse(coordinates, fill=(*color, alpha))

    def polygon(points, color=accent, alpha=220):
        draw.polygon([(int(x * SCALE), int(y * SCALE)) for x, y in points], fill=(*color, alpha))

    shift = rng.uniform(-18, 18)
    # A quiet, consistent framing system ties the many illustration families
    # together while each seed changes the composition, texture and rhythm.
    for _ in range(42):
        x, y = rng.uniform(20, 620), rng.uniform(22, 338)
        radius = rng.uniform(.5, 1.6)
        ellipse((x-radius, y-radius, x+radius, y+radius), light, rng.randint(12, 42))
    if theme == "piano":
        polygon([(145, 278), (485, 228), (498, 282), (158, 332)], middle)
        for index in range(17):
            x = 155 + index * 19
            polygon([(x, 229), (x+17, 226), (x+30, 286), (x+13, 289)], light, 235)
            if index % 7 not in (2, 6):
                polygon([(x+12, 228), (x+22, 226), (x+29, 259), (x+19, 261)], base, 255)
        for n in range(5):
            line([(95, 105+n*12), (535, 83+n*12)], accent, 1, 100)
        for n in range(7):
            x = 150 + n*48 + shift
            y = 126 - (n % 3)*17
            ellipse((x, y, x+12, y+8), light, 230)
            line([(x+11,y+3),(x+11,y-29)], light, 2)
    elif theme == "jazz":
        # Vinyl grooves plus brass-inspired curves evoke warm acoustic jazz.
        for r in range(30, 129, 8):
            ellipse((325-r,180-r,325+r,180+r), light, 40, True, 1)
        ellipse((298,153,352,207), accent, 235)
        ellipse((320,175,330,185), base, 255)
        for index in range(4):
            offset = index*10
            line([(140+offset,80),(140+offset,190),(161+offset,220),(205+offset,208),(223+offset,170)], accent, 4, 130+index*20)
        polygon([(210,168),(254,147),(247,191)], light, 225)
        line([(455,105),(480,105),(480,245),(435,245)], light, 3, 190)
    elif theme == "acoustic":
        ellipse((203+shift,130,343+shift,300), accent, 220)
        ellipse((281+shift,100,381+shift,240), accent, 220)
        polygon([(300+shift,178),(425+shift,55),(447+shift,74),(322+shift,199)], middle, 255)
        ellipse((282+shift,173,322+shift,213), base, 255)
        line([(290+shift,224),(317+shift,247)], light, 7)
        for index in range(6):
            line([(274+shift+index*4,246),(437+shift+index*2,63)], light, .8, 150)
        for index in range(3):
            ellipse((94-index*12,88-index*12,526+index*12,315+index*12), light, 28, True)
    elif theme == "rock":
        for index in range(5):
            x = 70+index*126
            polygon([(x,0),(x+12,0),(365,350),(260,350)],accent,24+index*8)
        polygon([(173,280),(211,184),(258,198),(312,138),(340,160),(300,226),(309,277),(233,313)],accent,240)
        polygon([(267,220),(438,57),(456,75),(285,238)],light,235)
        for index in range(5):
            line([(225+index*5,273),(448+index,66)],base,1,230)
        polygon([(399,165),(358,217),(388,216),(356,274),(423,202),(390,203)],light,200)
        line([(159,322),(485,322)],light,2,90)
    elif theme == "electronic":
        horizon = 210 + shift
        for index in range(11):
            y = horizon + (index/10)**2 * 150
            line([(0,y),(640,y)], accent, 1, 90)
        for x in range(-600, 1300, 100):
            line([(320,horizon),(x,360)], light, 1, 75)
        ellipse((237,47,403,213), accent, 170)
        for index in range(12):
            y = 125 + index*7
            line([(232,y),(408,y)], base, 3, 220)
        points = [(x,180+math.sin(x*.045+seed%30)*rng.uniform(14,40)) for x in range(60,581,4)]
        line(points, light, 3, 240)
    elif theme == "ambient":
        ellipse((395+shift,46,457+shift,108), light, 225)
        for index in range(5):
            top = 150+index*31
            points = [(0,360)] + [(x,top+math.sin(x/90+index+seed%10)*25) for x in range(0,650,8)] + [(640,360)]
            polygon(points, accent if index%2 else middle, 22+index*24)
        for index in range(9):
            y = 282+index*6
            line([(385-index*6,y),(458+index*6,y)], light, 1, 70-index*5)
    elif theme == "epic":
        for index in range(13):
            angle = math.pi*(index/12)
            line([(320,260),(320+290*math.cos(angle),260-220*math.sin(angle))], accent, 2, 65)
        polygon([(100,300),(245,101),(322,220),(404,74),(554,300)], middle, 255)
        polygon([(100,300),(245,101),(210,257),(278,300)], accent, 190)
        polygon([(322,220),(404,74),(380,237),(455,300)], light, 185)
        ellipse((277,68,363,154), light, 60, True, 2)
    elif theme == "dark":
        for index in range(9):
            x = 100+index*54+rng.uniform(-15,15)
            line([(x,360),(x+shift,60+rng.uniform(-25,35))], middle, 12, 180)
            line([(x,220),(x-40,155)], middle, 6, 180)
        ellipse((274,55,366,147), light, 150)
        polygon([(180,360),(301,180),(338,180),(472,360)], accent, 55)
        line([(320,180),(300,219),(336,241),(303,280),(321,330)], accent, 3, 200)
    elif theme == "romantic":
        for index in range(7):
            x, y = rng.uniform(150,490), rng.uniform(70,290)
            radius = rng.uniform(20,48)
            for petal in range(5):
                angle = petal*math.tau/5+shift/40
                dx, dy = math.cos(angle)*radius*.5, math.sin(angle)*radius*.5
                ellipse((x+dx-radius*.55,y+dy-radius*.55,x+dx+radius*.55,y+dy+radius*.55), accent if index%2 else light, 80+index*12)
            ellipse((x-4,y-4,x+4,y+4), light, 200)
    elif theme == "playful":
        for index in range(14):
            x, y = rng.uniform(70,570), rng.uniform(65,295)
            radius = rng.uniform(12,33)
            color = accent if index%2 else light
            if index%3 == 0:
                polygon([(x,y-radius),(x+radius,y+radius),(x-radius,y+radius)],color,220)
            elif index%3 == 1:
                ellipse((x-radius,y-radius,x+radius,y+radius),color,230,True,5)
            else:
                line([(x-radius,y),(x+radius,y)],color,5)
                line([(x,y-radius),(x,y+radius)],color,5)
        line([(80,285),(175,249),(230,276),(330,240),(390,269),(550,229)],light,3,110)
    elif theme == "world":
        for ring in range(4):
            r = 55+ring*25
            ellipse((320-r,180-r,320+r,180+r), accent if ring%2 else light, 150,True,2)
            for index in range(12+ring*6):
                a = index*math.tau/(12+ring*6)+shift/70
                x,y = 320+r*math.cos(a),180+r*math.sin(a)
                ellipse((x-3,y-3,x+3,y+3),light,200)
        polygon([(0,300),(110,245),(214,300),(330,252),(442,300),(550,245),(640,300),(640,360),(0,360)],accent,60)
    elif theme == "upbeat":
        ellipse((261+shift,72,393+shift,204),light,230)
        for index in range(12):
            angle=index*math.tau/12
            line([(327+shift+81*math.cos(angle),138+81*math.sin(angle)),(327+shift+100*math.cos(angle),138+100*math.sin(angle))],accent,3,210)
        for index in range(4):
            polygon([(0,360),(0,260+index*24),(170,225+index*22),(350,277+index*15),(510,208+index*32),(640,245+index*26),(640,360)],accent if index%2 else middle,90)
    else:
        ellipse((275+shift,59,409+shift,193),light,210)
        for index in range(4):
            polygon([(0,360),(90,260+index*15),(220,165+index*35),(345,268+index*14),(465,200+index*25),(640,260+index*20),(640,360)],accent if index%2 else middle,90+index*25)
        for index in range(3):
            line([(100,120+index*12),(550,120+index*12)],light,1,40)
    # Accent border and film grain unify the offline originals as one library.
    line([(22,48),(22,22),(64,22)],light,1,100)
    line([(576,338),(618,338),(618,312)],light,1,100)
    canvas = Image.alpha_composite(canvas.convert("RGBA"), layer)
    return canvas.convert("RGB").resize((WIDTH,HEIGHT),Image.Resampling.LANCZOS),theme


def main() -> None:
    manifest = json.loads((LIBRARY / "manifest.json").read_text(encoding="utf-8-sig"))
    entries = []
    for track in manifest["tracks"]:
        path = (LIBRARY / track["thumbnailFile"]).resolve()
        if not path.is_relative_to((LIBRARY / "thumbnails").resolve()):
            raise ValueError("Cover output escaped the packaged thumbnail directory")
        path.parent.mkdir(parents=True, exist_ok=True)
        image, theme = artwork(track)
        image.save(path, optimize=True)
        if len(entries) % 20 == 19:
            print(f"Created {len(entries) + 1} music covers", flush=True)
        entries.append({"id": track["id"], "theme": theme,
                        "file": track["thumbnailFile"],
                        "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    (LIBRARY / "artwork.json").write_text(json.dumps({
        "generator": "tools/GenerateMusicArtwork.py", "width": WIDTH, "height": HEIGHT,
        "description": "Original procedural genre illustrations; no third-party image assets.",
        "covers": entries,
    }, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    print(f"Authored {len(entries)} original genre covers in {LIBRARY / 'thumbnails'}")


if __name__ == "__main__":
    main()
