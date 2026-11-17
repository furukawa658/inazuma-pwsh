"""Render the README Before/After GIF from the measured README numbers."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

OUT = Path(__file__).resolve().parents[1] / "vscode-extension" / "media" / "before-after.gif"
W, H = 960, 440
BG, FG, DIM = (14, 17, 23), (240, 244, 248), (139, 148, 158)
RED, GREEN, YELLOW = (248, 81, 73), (63, 185, 80), (255, 214, 10)
FONT_DIR = Path("C:/Windows/Fonts")


def font(size, bold=False):
    return ImageFont.truetype(str(FONT_DIR / ("segoeuib.ttf" if bold else "segoeui.ttf")), size)


def ease(t):
    t = max(0.0, min(1.0, t))
    return 1 - (1 - t) ** 3


# (title, file, total chars, steps [(label, fraction)])
SCENES = [
    ("Read a 68,000-character design document", "peek_file + read_section",
     68000, [("peek_file", 0.07), ("+ read_section", 0.20)]),
    ("Read a 137,000-character execution log", "summarize_log",
     137000, [("summarize_log", 0.01)]),
]
BAR_X, BAR_W, BAR_H = 60, 840, 56


def draw_scene(d, scene, t):
    title, _, total, steps = scene
    d.text((60, 34), title, font=font(30, True), fill=FG)
    d.text((60, 82), "Characters your agent has to read", font=font(18), fill=DIM)

    d.text((60, 130), "BEFORE  full file", font=font(20, True), fill=RED)
    p = ease(t / 0.35)
    d.rounded_rectangle((BAR_X, 164, BAR_X + max(4, BAR_W * p), 164 + BAR_H), 8, fill=RED)
    if p > 0.95:
        d.text((BAR_X + 16, 176), f"{total:,} chars  (100%)", font=font(26, True), fill=BG)

    d.text((60, 250), "AFTER  Zuma MCP", font=font(20, True), fill=GREEN)
    d.rounded_rectangle((BAR_X, 284, BAR_X + BAR_W, 284 + BAR_H), 8, outline=(48, 54, 61), width=2)
    q = ease((t - 0.45) / 0.35)
    frac = steps[0][1]
    shown = frac * q
    if len(steps) > 1:
        r = ease((t - 0.8) / 0.15)
        shown = frac + (steps[1][1] - frac) * r if t > 0.8 else shown
    if shown > 0:
        d.rounded_rectangle((BAR_X, 284, BAR_X + max(4, BAR_W * shown), 284 + BAR_H), 8, fill=GREEN)
    if t > 0.6:
        final = steps[-1][1] if t > 0.92 else steps[0][1]
        label = steps[-1][0] if t > 0.92 else steps[0][0]
        big = f"{round(final * 100)}%"
        x = BAR_X + max(BAR_W * shown, 60) + 20
        d.text((x, 280), big, font=font(52, True), fill=YELLOW)
        d.text((x + (130 if final >= 0.1 else 100), 300), f"~{int(total * final):,} chars  ({label})",
               font=font(20), fill=FG)
    d.text((60, 392), "Single-file measurements, not whole-chat savings.", font=font(16), fill=DIM)


def end_card(d, t):
    p = ease(t / 0.4)
    d.text((60, 110), "Read less.", font=font(54, True), fill=FG)
    d.text((60, 190), "7%  /  1%", font=font(96, True), fill=YELLOW if p > 0.5 else DIM)
    d.text((60, 320), "of the characters.", font=font(40, True), fill=GREEN)
    d.text((60, 392), "Zuma MCP  -  peek_file / read_section / summarize_log", font=font(18), fill=DIM)


frames, durations = [], []


def add(draw_fn, count, ms):
    for i in range(count):
        img = Image.new("RGB", (W, H), BG)
        draw_fn(ImageDraw.Draw(img), i / max(1, count - 1))
        frames.append(img)
        durations.append(ms)


for scene in SCENES:
    add(lambda d, t, s=scene: draw_scene(d, s, t), 28, 55)
    add(lambda d, t, s=scene: draw_scene(d, s, 1.0), 1, 1400)
add(end_card, 12, 55)
add(lambda d, t: end_card(d, 1.0), 1, 2200)

pal = [f.quantize(colors=64, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE) for f in frames]
OUT.parent.mkdir(parents=True, exist_ok=True)
pal[0].save(OUT, save_all=True, append_images=pal[1:], duration=durations, loop=0, optimize=True, disposal=1)
print(OUT, OUT.stat().st_size, "bytes", len(frames), "frames")


