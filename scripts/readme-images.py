"""Builds the README's pictures in docs/images from the app's own screenshots.

Run the screenshot tool first (it draws every window, light and dark, with no real app running):
    dotnet run --project tools/GlobalAutocorrect.Screens -c Debug
then:
    python scripts/readme-images.py

Writes docs/images/<page>-<light|dark>.png (each window framed on the app's accent colours), logo.png and
social-preview.png (1280 x 640, for the repository's Settings > Social preview). Needs Pillow.
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

REPO = Path(__file__).resolve().parent.parent
SCREENS = REPO / "artifacts" / "screens"
OUT = REPO / "docs" / "images"
FONTS = Path("C:/Windows/Fonts")

# The app's palette (src/GlobalAutocorrect.App/Theme/Palette.cs).
ACCENT, ACCENT_LIGHT, ACCENT_DARK = "#1F6FEB", "#5B9BFF", "#134FB0"
BACKDROP = {
    "light": ("#E8F0FE", "#C9DCFF"),   # top-left, bottom-right
    "dark": ("#0B1220", "#13284D"),
}

# Which windows the README shows, and the scale they are drawn at (125% reads well at GitHub's width).
PAGES = ["home", "clean-up", "prediction", "apps", "dictionary", "about", "firstrun-1"]
SCALE = 125


def rgb(h: str) -> tuple[int, int, int]:
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def gradient(size: tuple[int, int], a: str, b: str) -> Image.Image:
    """A diagonal gradient from a (top left) to b (bottom right)."""
    w, h = size
    ca, cb = rgb(a), rgb(b)
    small = Image.new("RGB", (64, 64))
    px = small.load()
    for y in range(64):
        for x in range(64):
            t = (x + y) / 126
            px[x, y] = tuple(round(ca[i] + (cb[i] - ca[i]) * t) for i in range(3))
    return small.resize(size, Image.BICUBIC)


def rounded(img: Image.Image, radius: int) -> Image.Image:
    mask = Image.new("L", img.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, img.width - 1, img.height - 1), radius, fill=255)
    out = img.convert("RGBA")
    out.putalpha(mask)
    return out


def framed(shot: Image.Image, mode: str, pad: int = 56, radius: int = 14) -> Image.Image:
    """The window with rounded corners and a soft shadow, on the accent backdrop."""
    w, h = shot.width + pad * 2, shot.height + pad * 2
    canvas = gradient((w, h), *BACKDROP[mode]).convert("RGBA")

    shadow = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    alpha = 110 if mode == "light" else 170
    ImageDraw.Draw(shadow).rounded_rectangle(
        (pad, pad + 14, pad + shot.width, pad + shot.height + 14), radius, fill=(8, 20, 48, alpha))
    canvas.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(22)))

    window = rounded(shot, radius)
    border = Image.new("RGBA", window.size, (0, 0, 0, 0))
    edge = (255, 255, 255, 40) if mode == "dark" else (11, 18, 32, 30)
    ImageDraw.Draw(border).rounded_rectangle((0, 0, window.width - 1, window.height - 1), radius, outline=edge, width=1)
    canvas.alpha_composite(window, (pad, pad))
    canvas.alpha_composite(border, (pad, pad))
    return canvas.convert("RGB")


def logo() -> Image.Image:
    ico = Image.open(REPO / "assets" / "icons" / "app.ico")
    best = max(ico.info.get("sizes", [ico.size]))
    ico.size = best
    return ico.convert("RGBA")


def font(name: str, size: int) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(str(FONTS / name), size)


def social_preview(home_dark: Image.Image, icon: Image.Image) -> Image.Image:
    w, h = 1280, 640
    img = gradient((w, h), "#0B1220", "#16336B").convert("RGBA")

    # A soft accent glow behind the window.
    glow = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse((640, 60, 1360, 700), fill=(*rgb(ACCENT), 120))
    img.alpha_composite(glow.filter(ImageFilter.GaussianBlur(120)))

    # The Home window, large, running off the right edge.
    shot = home_dark.copy()
    shot = shot.resize((int(shot.width * 0.62), int(shot.height * 0.62)), Image.LANCZOS)
    win = rounded(shot, 14)
    shadow = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    x, y = 700, 120
    ImageDraw.Draw(shadow).rounded_rectangle((x, y + 16, x + win.width, y + win.height + 16), 14, fill=(0, 0, 0, 180))
    img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(24)))
    img.alpha_composite(win, (x, y))

    d = ImageDraw.Draw(img)
    ic = icon.resize((84, 84), Image.LANCZOS)
    img.alpha_composite(ic, (72, 92))
    d.text((72, 200), "Global", font=font("seguibl.ttf", 74), fill="#FFFFFF")
    d.text((72, 282), "Autocorrect", font=font("seguibl.ttf", 74), fill=ACCENT_LIGHT)
    d.text((74, 392), "Typo-free typing in every Windows app.", font=font("seguisb.ttf", 30), fill="#E8EEF8")
    d.text((74, 436), "A private AI that runs on your own GPU.", font=font("segoeui.ttf", 28), fill="#A9B6CC")

    cx = 74
    for chip in ("100% on-device", "~0.3 s fixes", "Tab to finish words"):
        f = font("seguisb.ttf", 21)
        tw = d.textlength(chip, font=f)
        d.rounded_rectangle((cx, 510, cx + tw + 32, 552), 21, fill=(31, 111, 235, 60), outline=(91, 155, 255, 160))
        d.text((cx + 16, 518), chip, font=f, fill="#FFFFFF")
        cx += tw + 46
    return img.convert("RGB")


# ---------------------------------------------------------------------------------------------------------------
# The animated demo (docs/images/demo-<mode>.svg): a message typed with slips, fixed the way the app fixes them.
# SMIL animations that all share one duration, so every part stays in step and the loop repeats cleanly; GitHub
# shows an SVG through <img>, where SMIL runs and scripts don't. The text is monospaced and each line is held to
# an exact width (textLength), so the caret and underlines land on the right letters whatever font is installed.
# ---------------------------------------------------------------------------------------------------------------
LOOP = 14.0          # seconds
KEY = 0.075          # seconds per typed key
CW = 12.6            # character width, px
FS = 21              # font size, px
X0, Y1, Y2 = 44, 150, 196   # text origin and the baselines of the two lines

DEMO_THEME = {
    "light": dict(bg="#F7F9FC", win="#FFFFFF", bar="#F1F4F9", line="#E3E8F0", text="#0B1220", muted="#4A5568",
                  ghost="#9AA6B8", accent=ACCENT, pill="#E8F0FE", pillText="#134FB0", caret="#0B1220"),
    "dark": dict(bg="#0B1220", win="#172131", bar="#1C2739", line="#26324A", text="#E8EEF8", muted="#A9B6CC",
                 ghost="#5E6C84", accent=ACCENT_LIGHT, pill="#1A2E52", pillText="#BFD6FF", caret="#E8EEF8"),
}


def _kt(t: float) -> str:
    return f"{max(0.0, min(1.0, t / LOOP)):.4f}"


def _discrete(attr: str, points: list[tuple[float, str]]) -> str:
    """A value that jumps at the given times, repeating every LOOP. points: (time, value), the first at 0."""
    if points[0][0] != 0:
        raise ValueError("the first point must be at time 0")
    times = ";".join(_kt(t) for t, _ in points)
    values = ";".join(v for _, v in points)
    return (f'<animate attributeName="{attr}" dur="{LOOP}s" repeatCount="indefinite" calcMode="discrete" '
            f'keyTimes="{times}" values="{values}"/>')


def _visible(start: float, end: float, fade: float = 0.0) -> str:
    """Opacity 1 from start to end (fading in and out over `fade` seconds), 0 otherwise."""
    if fade == 0:
        pts = [(0, "0"), (start, "1"), (end, "0")] if start > 0 else [(0, "1"), (end, "0")]
        return _discrete("opacity", pts)
    pts = [(0, 0), (start, 0), (start + fade, 1), (end - fade, 1), (end, 0), (LOOP, 0)]
    return (f'<animate attributeName="opacity" dur="{LOOP}s" repeatCount="indefinite" '
            f'keyTimes="{";".join(_kt(t) for t, _ in pts)}" values="{";".join(str(v) for _, v in pts)}"/>')


def _esc(s: str) -> str:
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("'", "&#39;")


def demo_svg(mode: str) -> str:
    c = DEMO_THEME[mode]
    W, H = 860, 330
    parts: list[str] = []
    clips: list[str] = []
    caret: list[tuple[float, float, float]] = []   # (time, x, baseline)

    def line(text: str, y: float, start: float, end: float, typed_from: int = -1, typed_at: float = 0,
             colour: str | None = None, ghost: tuple[int, int] | None = None) -> float:
        """Shows `text` from start to end. Characters from typed_from on appear one key at a time from typed_at.
        Returns when the last key lands."""
        n = len(text)
        attrs = f'x="{X0}" y="{y}" textLength="{n * CW:.1f}" lengthAdjust="spacing"'
        body = _esc(text)
        if ghost:   # characters drawn faint: a suggestion not yet taken
            g0, g1 = ghost
            body = (_esc(text[:g0]) + f'<tspan fill="{c["ghost"]}">{_esc(text[g0:g1])}</tspan>' + _esc(text[g1:]))
        done = typed_at
        clip_attr = ""
        if typed_from >= 0:
            cid = f"c{len(clips)}"
            pts = [(0.0, f"{typed_from * CW:.1f}")]
            for i in range(typed_from, n):
                t = typed_at + (i - typed_from) * KEY
                pts.append((t, f"{(i + 1) * CW:.1f}"))
                caret.append((t, X0 + (i + 1) * CW, y))
            done = typed_at + (n - typed_from - 1) * KEY
            clips.append(f'<clipPath id="{cid}"><rect x="{X0}" y="{y - 30}" height="44" width="{typed_from * CW:.1f}">'
                         f'{_discrete("width", pts)}</rect></clipPath>')
            clip_attr = f' clip-path="url(#{cid})"'
        parts.append(f'<g opacity="0"{clip_attr}>{_visible(start, end)}'
                     f'<text {attrs} fill="{colour or c["text"]}">{body}</text></g>')
        return done

    def underline(y: float, a: int, b: int, at: float) -> None:
        x, w = X0 + a * CW, (b - a) * CW
        parts.append(f'<rect x="{x:.1f}" y="{y + 6}" width="{w:.1f}" height="2.5" rx="1.2" fill="{c["accent"]}" opacity="0">'
                     f'{_visible(at, at + 1.6, 0.25)}</rect>')

    def pill(label: str, detail: str, start: float, end: float) -> None:
        lw = len(label) * 8.4 + 22
        dw = len(detail) * 7.6 + 24
        parts.append(
            f'<g opacity="0">{_visible(start, end, 0.2)}'
            f'<rect x="{X0 - 4}" y="{H - 66}" rx="15" width="{lw + dw:.0f}" height="30" fill="{c["pill"]}"/>'
            f'<rect x="{X0 - 4}" y="{H - 66}" rx="15" width="{lw:.0f}" height="30" fill="{c["accent"]}"/>'
            f'<text x="{X0 + 7}" y="{H - 46}" class="ui b" fill="#FFFFFF">{_esc(label)}</text>'
            f'<text x="{X0 - 4 + lw + 12:.0f}" y="{H - 46}" class="ui" fill="{c["pillText"]}">{_esc(detail)}</text></g>')

    # Line 1: typed with slips, two instant fixes as words finish, then the AI tidies the sentence on a pause.
    a = "Hi team, thnaks"
    b = "Hi team, thanks for teh"
    s = "Hi team, thanks for the update. Ill send it over by friday ."
    d = "Hi team, thanks for the update. I'll send it over by Friday."
    t = 0.6
    fix1 = t + len(a) * KEY + 0.04                          # the space after "thnaks": fixed at once
    line(a, Y1, t, fix1, 0, t)
    fix2 = fix1 + (len(b) - 15) * KEY + 0.04                # the space after "teh"
    line(b, Y1, fix1, fix2, 15, fix1)
    caret.append((fix1, X0 + 16 * CW, Y1))
    underline(Y1, 9, 15, fix1)
    ai = fix2 + (len(s) - 23) * KEY + 0.9                   # the pause after the last key
    line(s, Y1, fix2, ai, 23, fix2)
    caret.append((fix2, X0 + 24 * CW, Y1))
    underline(Y1, 20, 23, fix2)
    line(d, Y1, ai, LOOP - 0.5)
    caret.append((ai, X0 + len(d) * CW, Y1))
    underline(Y1, 32, 36, ai)
    underline(Y1, 53, 60, ai)

    # Line 2: a suggestion after the caret, taken with Tab.
    two = ai + 1.5
    typed = "Best wi"
    sugg = two + len(typed) * KEY + 0.45
    line(typed, Y2, two, sugg, 0, two)
    line("Best wishes", Y2, sugg, sugg + 1.4, ghost=(7, 11))
    caret.append((sugg, X0 + 7 * CW, Y2))
    tab = sugg + 1.4
    line("Best wishes", Y2, tab, LOOP - 0.5)
    caret.append((tab, X0 + 11 * CW, Y2))
    underline(Y2, 7, 11, tab)
    # The Tab key cap beside the suggestion.
    parts.append(f'<g opacity="0">{_visible(sugg + 0.1, tab + 0.1, 0.15)}'
                 f'<rect x="{X0 + 11 * CW + 10:.1f}" y="{Y2 - 19}" width="44" height="24" rx="6" fill="none" '
                 f'stroke="{c["ghost"]}" stroke-width="1.5"/>'
                 f'<text x="{X0 + 11 * CW + 32:.1f}" y="{Y2 - 2}" text-anchor="middle" class="ui s" fill="{c["muted"]}">Tab</text></g>')

    pill("Instant fix", "thnaks → thanks, the moment the word ends", fix1, fix2)
    pill("Instant fix", "teh → the", fix2, ai - 0.9)
    pill("Waiting", "you paused, so the local AI reads the sentence", ai - 0.9, ai)
    pill("AI clean-up", "3 fixes in about 0.3 s, on your own GPU", ai, sugg)
    pill("Suggestion", "press Tab to take it, or keep typing", sugg, tab)
    pill("Tab", "word finished for you", tab, LOOP - 0.5)

    # The caret: follows the typing, blinks while idle, hidden for the fade at the end of the loop.
    caret.sort()
    xs = [(0.0, f"{X0}")] + [(tm, f"{x:.1f}") for tm, x, _ in caret]
    ys = [(0.0, f"{Y1 - 20}")] + [(tm, f"{y - 20}") for tm, _, y in caret]
    parts.append(f'<g>{_visible(0.001, LOOP - 0.5)}<rect width="2" height="26" fill="{c["caret"]}">'
                 f'{_discrete("x", xs)}{_discrete("y", ys)}'
                 f'<animate attributeName="opacity" values="1;1;0;0" keyTimes="0;0.5;0.5;1" dur="1.06s" repeatCount="indefinite"/>'
                 f'</rect></g>')

    chrome = (
        f'<rect x="12" y="12" width="{W - 24}" height="{H - 24}" rx="14" fill="{c["win"]}" stroke="{c["line"]}"/>'
        f'<path d="M12 26a14 14 0 0 1 14-14h{W - 52}a14 14 0 0 1 14 14v26H12z" fill="{c["bar"]}"/>'
        f'<line x1="12" y1="52" x2="{W - 12}" y2="52" stroke="{c["line"]}"/>'
        f'<text x="{X0 - 6}" y="37" class="ui s" fill="{c["muted"]}">New message  ·  any Windows app</text>'
        + "".join(f'<text x="{W - 112 + i * 34}" y="38" class="ui" fill="{c["muted"]}">{g}</text>' for i, g in enumerate("–□✕"))
        + f'<text x="{X0}" y="92" class="ui" fill="{c["muted"]}">Subject: Thursday\'s update</text>'
        f'<line x1="{X0}" y1="108" x2="{W - 44}" y2="108" stroke="{c["line"]}"/>'
    )
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}" role="img" '
        f'aria-label="Typing with slips in any app: thnaks and teh are fixed as each word ends, the AI tidies the '
        f'sentence on a pause, and Tab finishes a suggested word.">'
        f'<style>text{{font-family:"Cascadia Mono",Consolas,"SF Mono",Menlo,monospace;font-size:{FS}px}}'
        f'.ui{{font-family:"Segoe UI Variable Text","Segoe UI",system-ui,-apple-system,sans-serif;font-size:14px}}'
        f'.b{{font-weight:600}}.s{{font-size:13px}}</style>'
        f'<defs>{"".join(clips)}</defs>'
        f'{chrome}{"".join(parts)}</svg>'
    )


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for mode in ("light", "dark"):
        (OUT / f"demo-{mode}.svg").write_text(demo_svg(mode), encoding="utf-8")
        print(f"demo-{mode}.svg")
    for page in PAGES:
        for mode in ("light", "dark"):
            src = SCREENS / f"{page}-{mode}-{SCALE}.png"
            framed(Image.open(src).convert("RGB"), mode).save(OUT / f"{page}-{mode}.png", optimize=True)
            print(f"{page}-{mode}.png")

    icon = logo()
    icon.resize((256, 256), Image.LANCZOS).save(OUT / "logo.png", optimize=True)
    home = Image.open(SCREENS / "home-dark-150.png").convert("RGB")
    social_preview(home, icon).save(OUT / "social-preview.png", optimize=True)
    print("logo.png, social-preview.png")


if __name__ == "__main__":
    main()
