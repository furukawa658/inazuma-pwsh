"""Render the README savings GIF: an illustration of the status bar item with measured numbers."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

OUT = Path(__file__).resolve().parents[1] / "vscode-extension" / "media" / "savings.gif"
W, H = 960, 440
BG, FG, DIM, TRACK = (14, 17, 23), (240, 244, 248), (139, 148, 158), (48, 54, 61)
RED, GREEN, YELLOW, BAR_BLUE, WHITE = (248, 81, 73), (63, 185, 80), (255, 214, 10), (0, 122, 204), (255, 255, 255)
FONT_DIR = Path("C:/Windows/Fonts")

# (tool, bytes read, bytes returned), from usage.jsonl after calling the 0.6.0 server on
# PlaywrightTraceParser.cs (peek_file, read_section) and a normal-verbosity build log of this repository.
CALLS = [("peek_file", 44692, 3030), ("read_section", 44692, 4795), ("summarize_log", 173908, 5646)]
ROW_Y, ROW_H, TRACK_X, TRACK_W = 128, 66, 500, 300


def font(size, bold=False):
    return ImageFont.truetype(str(FONT_DIR / ("segoeuib.ttf" if bold else "segoeui.ttf")), size)


def ease(t):
    t = max(0.0, min(1.0, t))
    return 1 - (1 - t) ** 3


def kb(value):
    return f"{value / 1024:.1f} KB"


def saved(done):
    read = sum(call[1] for call in CALLS[:done])
    returned = sum(call[2] for call in CALLS[:done])
    return round((1 - returned / read) * 100) if read else None


def status_bar(d, percent, highlight):
    d.rectangle((0, H - 40, W, H), fill=BAR_BLUE)
    d.text((16, H - 31), "VS Code status bar", font=font(15), fill=(200, 225, 245))
    if percent is None:
        return
    text = f"{percent}% saved"
    width = d.textlength(text, font=font(18, True))
    left = W - 36 - width - 26
    if highlight:
        d.rounded_rectangle((left - 12, H - 37, W - 20, H - 3), 6, fill=(0, 92, 160), outline=YELLOW, width=2)
    d.polygon([(left + 10, H - 32), (left, H - 18), (left + 7, H - 18), (left + 3, H - 8), (left + 15, H - 23), (left + 8, H - 23)], fill=YELLOW)
    d.text((left + 24, H - 33), text, font=font(18, True), fill=WHITE)


def draw_calls(d, done, t):
    """Rows before `done` are complete; row `done` animates with t in 0..1."""
    d.text((60, 26), "See how much Zuma MCP saves", font=font(30, True), fill=FG)
    d.text((60, 72), "For each tool call: bytes it read, and bytes it returned to your agent", font=font(18), fill=DIM)
    for index, (tool, read, returned) in enumerate(CALLS[:done + 1]):
        progress = 1.0 if index < done else t
        y = ROW_Y + index * ROW_H
        d.text((60, y + 8), tool, font=font(22, True), fill=FG)
        d.text((250, y + 12), f"{kb(read)}  to  {kb(returned)}" if progress > 0.5 else f"read {kb(read)}", font=font(18), fill=DIM)
        fraction = 1 - (1 - returned / read) * ease((progress - 0.2) / 0.6)
        d.rounded_rectangle((TRACK_X, y + 8, TRACK_X + TRACK_W, y + 38), 6, outline=TRACK, width=2)
        d.rounded_rectangle((TRACK_X, y + 8, TRACK_X + max(6, TRACK_W * fraction), y + 38), 6, fill=GREEN if progress > 0.8 else RED)
        if progress > 0.8:
            d.text((TRACK_X + TRACK_W + 24, y + 2), f"{round((1 - returned / read) * 100)}%", font=font(30, True), fill=YELLOW)
    d.text((60, H - 76), "Measured on this repository: a 44 KB C# file and a 170 KB build log. Bytes, not tokens.", font=font(15), fill=DIM)
    finished = done + (1 if t > 0.8 else 0)
    status_bar(d, saved(finished), t > 0.8 and finished > 0)


def end_card(d, t):
    d.text((60, 60), "Now visible in the status bar", font=font(34, True), fill=FG)
    d.text((60, 130), f"{saved(len(CALLS))}% saved", font=font(96, True), fill=YELLOW if ease(t / 0.4) > 0.5 else DIM)
    d.text((60, 270), "Click it for the breakdown by tool.", font=font(30, True), fill=GREEN)
    d.text((60, H - 76), "Illustration of the status bar item. The numbers are the measured calls shown before.", font=font(15), fill=DIM)
    status_bar(d, saved(len(CALLS)), True)


frames, durations = [], []


def add(draw_fn, count, ms):
    for i in range(count):
        img = Image.new("RGB", (W, H), BG)
        draw_fn(ImageDraw.Draw(img), i / max(1, count - 1))
        frames.append(img)
        durations.append(ms)


for step in range(len(CALLS)):
    add(lambda d, t, s=step: draw_calls(d, s, t), 22, 55)
    add(lambda d, t, s=step: draw_calls(d, s, 1.0), 1, 1100)
add(end_card, 12, 55)
add(lambda d, t: end_card(d, 1.0), 1, 2600)

pal = [f.quantize(colors=64, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE) for f in frames]
OUT.parent.mkdir(parents=True, exist_ok=True)
pal[0].save(OUT, save_all=True, append_images=pal[1:], duration=durations, loop=0, optimize=True, disposal=1)
print(OUT, OUT.stat().st_size, "bytes", len(frames), "frames")
