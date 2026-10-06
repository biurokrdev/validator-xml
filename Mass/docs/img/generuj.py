"""Rysunki do Mass/docs/img. Wszystkie wymiary w mm pochodzą z EnvelopeLayouts.C65TwoWindows."""
import io, os, sys

OUT = sys.argv[1]
os.makedirs(OUT, exist_ok=True)

BLUE, BLUE_L = '#1E5AA8', '#DCE8F7'
RED, RED_L = '#D50000', '#FBE1E1'
GREEN, GREEN_L = '#1B8A3A', '#DDF2E3'
INK, MUTED, LINE = '#1C2333', '#5A6782', '#9AA5BA'
KRAFT, KRAFT_D = '#F1E3C6', '#C9B48A'
PAPER = '#FFFFFF'
FONT = "font-family=\"Segoe UI, Roboto, Helvetica, Arial, sans-serif\""

# koperta C65 i kartka A4 złożona na trzy
ENV_W, ENV_H = 229, 114
SHEET_W, SHEET_H = 210, 99
REC_WIN = (119, 54, 90, 45)     # okno adresata na kopercie: 20 mm od prawej, 15 mm od dołu
SND_WIN = (28, 64, 70, 30)      # okno nadawcy na kopercie: 28 mm od lewej, 20 mm od dołu
REC_AREA = (119, 54, 71, 30)    # obszar strony widoczny zawsze
SND_AREA = (28, 64, 51, 15)
REC_OK = (120, 55, 69, 28)      # po odjęciu 1 mm odstępu
SND_OK = (29, 65, 49, 13)


class Svg:
    def __init__(self, w, h, title):
        self.w, self.h = w, h
        self.p = [
            f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" width="{w}" height="{h}" role="img" aria-label="{title}">',
            f'<title>{title}</title>',
            '<defs><marker id="arr" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">'
            f'<path d="M0,0 L10,5 L0,10 z" fill="{MUTED}"/></marker>'
            '<filter id="sh" x="-5%" y="-5%" width="110%" height="115%"><feDropShadow dx="0" dy="2" stdDeviation="3" flood-opacity="0.18"/></filter></defs>',
            f'<rect width="{w}" height="{h}" fill="#F7F9FC"/>',
        ]

    def rect(self, x, y, w, h, fill='none', stroke='none', sw=1.5, dash=None, rx=0, extra=''):
        d = f' stroke-dasharray="{dash}"' if dash else ''
        self.p.append(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" rx="{rx}" fill="{fill}" stroke="{stroke}" stroke-width="{sw}"{d} {extra}/>')

    def line(self, x1, y1, x2, y2, stroke=LINE, sw=1, dash=None, arrows=False):
        d = f' stroke-dasharray="{dash}"' if dash else ''
        a = ' marker-start="url(#arr)" marker-end="url(#arr)"' if arrows else ''
        self.p.append(f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" stroke="{stroke}" stroke-width="{sw}"{d}{a}/>')

    def text(self, x, y, s, size=13, fill=INK, anchor='start', weight='normal', rotate=None, style=''):
        r = f' transform="rotate({rotate} {x:.1f} {y:.1f})"' if rotate is not None else ''
        self.p.append(f'<text x="{x:.1f}" y="{y:.1f}" {FONT} font-size="{size}" fill="{fill}" text-anchor="{anchor}" font-weight="{weight}"{r} {style}>{s}</text>')

    def dim_h(self, x1, x2, y, label, color=MUTED, above=True):
        self.line(x1, y, x2, y, stroke=color, arrows=True)
        self.text((x1 + x2) / 2, y - 6 if above else y + 16, label, size=12, fill=color, anchor='middle')

    def dim_v(self, x, y1, y2, label, color=MUTED, left=True):
        self.line(x, y1, x, y2, stroke=color, arrows=True)
        tx = x - 7 if left else x + 16
        self.text(tx, (y1 + y2) / 2, label, size=12, fill=color, anchor='middle', rotate=-90)

    def raw(self, s):
        self.p.append(s)

    def save(self, name):
        self.p.append('</svg>')
        io.open(os.path.join(OUT, name), 'w', encoding='utf-8', newline='\n').write('\n'.join(self.p) + '\n')
        print(name)


def bars(svg, x, y, w, h, n=58, color=INK):
    """Umowny kod kreskowy: stały wzór, nie jest prawdziwym kodem."""
    pattern = [2, 1, 1, 3, 1, 2, 1, 1, 2, 3, 1, 1, 2, 2, 1, 3, 1, 1, 1, 2, 3, 1, 2, 1, 1, 2, 1, 3, 2, 1]
    total = sum(pattern[i % len(pattern)] for i in range(n))
    unit = w / total
    cx, black = x, True
    for i in range(n):
        bw = pattern[i % len(pattern)] * unit
        if black:
            svg.rect(cx, y, bw, h, fill=color)
        cx += bw
        black = not black


def label(svg, x, y, w, h, number='(00)75900773 1 51200062 1'):
    """Uproszczona nalepka R w prostokącie x, y, w, h (px)."""
    svg.rect(x, y, w, h, fill=PAPER, stroke=LINE, sw=0.8)
    pad = h * 0.06
    bar_h = (h - 2 * pad) * 0.74
    r_w = w * 0.2
    svg.text(x + pad, y + pad + bar_h, 'R', size=bar_h * 1.32, fill=RED, weight='700')
    bars(svg, x + pad + r_w + w * 0.04, y + pad, w - r_w - 2 * pad - w * 0.08, bar_h)
    svg.text(x + w / 2, y + h - pad * 1.2, number, size=max(5, h * 0.17), anchor='middle')


# ============================================================ 1. koperta
def envelope():
    S, ox, oy = 3.0, 90, 80
    s = Svg(int(ENV_W * S + ox + 70), int(ENV_H * S + oy + 150), 'Koperta C65 z dwoma okienkami')
    X = lambda mm: ox + mm * S
    Y = lambda mm: oy + mm * S
    s.text(ox, 34, 'Koperta C65 z dwoma okienkami, widok od frontu', size=19, weight='700')
    s.text(ox, 56, 'wymiary w milimetrach, rysunek w skali', size=13, fill=MUTED)
    s.rect(X(0), Y(0), ENV_W * S, ENV_H * S, fill=KRAFT, stroke=KRAFT_D, sw=2, rx=6, extra='filter="url(#sh)"')

    for (x, y, w, h), col, fill, t1, t2 in (
        (SND_WIN, RED, RED_L, 'OKNO NADAWCY', '70 × 30 mm · nalepka R'),
        (REC_WIN, BLUE, BLUE_L, 'OKNO ADRESATA', '90 × 45 mm · adres adresata'),
    ):
        s.rect(X(x), Y(y), w * S, h * S, fill=fill, stroke=col, sw=2.5, rx=5)
        s.text(X(x + w / 2), Y(y + h / 2) - 4, t1, size=15, fill=col, anchor='middle', weight='700')
        s.text(X(x + w / 2), Y(y + h / 2) + 16, t2, size=13, fill=col, anchor='middle')

    # wymiary koperty
    s.dim_h(X(0), X(ENV_W), Y(ENV_H) + 62, '229', above=False)
    s.dim_v(X(0) - 52, Y(0), Y(ENV_H), '114')
    # odległości okien od krawędzi
    yb = Y(ENV_H) + 24
    s.line(X(28), Y(94), X(28), yb + 4, dash='3 3')
    s.dim_h(X(0), X(28), yb, '28', color=RED, above=False)
    s.line(X(209), Y(99), X(209), yb + 4, dash='3 3')
    s.dim_h(X(209), X(ENV_W), yb, '20', color=BLUE, above=False)
    s.dim_v(X(63), Y(94), Y(ENV_H), '20', color=RED, left=False)
    s.dim_v(X(164), Y(99), Y(ENV_H), '15', color=BLUE, left=False)

    ly = Y(ENV_H) + 100
    s.rect(ox, ly - 12, 16, 16, fill=RED_L, stroke=RED, sw=2, rx=3)
    s.text(ox + 24, ly + 1, 'okno nadawcy: 28 mm od lewej, 20 mm od dołu', size=13)
    s.rect(ox + 360, ly - 12, 16, 16, fill=BLUE_L, stroke=BLUE, sw=2, rx=3)
    s.text(ox + 384, ly + 1, 'okno adresata: 20 mm od prawej, 15 mm od dołu', size=13)
    s.save('koperta-c65.svg')


# ============================================================ 2. strona
def page():
    S, ox, oy = 3.4, 86, 96
    shown_h = 112
    s = Svg(int(SHEET_W * S + ox + 40), int(shown_h * S + oy + 96), 'Pierwsza strona pisma: gdzie ma być adres, a gdzie nalepka R')
    X = lambda mm: ox + mm * S
    Y = lambda mm: oy + mm * S
    s.text(ox, 32, 'Pierwsza strona pisma (A4): gdzie ma być adres, a gdzie nalepka R', size=19, weight='700')
    s.text(ox, 54, 'milimetry od lewej i od górnej krawędzi strony, rysunek w skali', size=13, fill=MUTED)

    s.rect(X(0), Y(0), SHEET_W * S, shown_h * S, fill=PAPER, stroke=LINE, sw=1.2, extra='filter="url(#sh)"')
    # papier firmowy, data, treść: tylko dla orientacji
    s.text(X(25), Y(13), 'URZĄD GMINY WÓLKA', size=13, weight='700', fill=MUTED)
    s.text(X(25), Y(18), 'ul. Polna 1, 21-100 Lubartów', size=10, fill=MUTED)
    s.text(X(190), Y(30), 'Lubartów, 28 września 2026 r.', size=11, fill=MUTED, anchor='end')
    for i, wmm in enumerate((70, 150, 165, 120)):
        s.rect(X(25), Y(93 + i * 4.6), wmm * S, 4, fill='#E3E8F1', rx=2)

    # linia zgięcia
    s.line(X(0), Y(99), X(SHEET_W), Y(99), stroke=LINE, dash='8 5')
    s.text(X(SHEET_W) - 6, Y(99) - 6, 'linia zgięcia: 99 mm', size=11, fill=MUTED, anchor='end')

    # okno nadawcy / nalepka
    x, y, w, h = SND_AREA
    s.rect(X(x), Y(y), w * S, h * S, stroke=RED, sw=1.6, dash='6 4')
    x, y, w, h = SND_OK
    s.rect(X(x), Y(y), w * S, h * S, fill=RED_L, stroke=RED, sw=1)
    label(s, X(29.5), Y(65.5), 48 * S, 12 * S)
    s.text(X(28), Y(64) - 8, 'NALEPKA R (grafika), najwyżej 49 × 13 mm', size=12.5, fill=RED, weight='700')

    # okno adresata / adres
    x, y, w, h = REC_AREA
    s.rect(X(x), Y(y), w * S, h * S, stroke=BLUE, sw=1.6, dash='6 4')
    x, y, w, h = REC_OK
    s.rect(X(x), Y(y), w * S, h * S, fill=BLUE_L, stroke=BLUE, sw=1)
    for i, t in enumerate(('Pan Jan Kowalski', 'ul. Marszałkowska 142 m. 5', '00-061 Warszawa')):
        s.text(X(123), Y(61.5 + i * 4.6), t, size=13.5)
    s.text(X(119), Y(54) - 8, 'ADRES ADRESATA (tekst), najwyżej 69 × 28 mm', size=12.5, fill=BLUE, weight='700')

    # podziałka pozioma
    ry = Y(0) - 14
    s.line(X(0), ry, X(SHEET_W), ry, stroke=LINE)
    for mm, col in ((0, MUTED), (29, RED), (78, RED), (120, BLUE), (189, BLUE), (210, MUTED)):
        s.line(X(mm), ry - 5, X(mm), ry + 5, stroke=col, sw=1.5)
        s.text(X(mm), ry - 9, str(mm), size=12, fill=col, anchor='middle', weight='700')
    # podziałka pionowa
    rx = X(0) - 14
    s.line(rx, Y(0), rx, Y(shown_h), stroke=LINE)
    for mm, col, dx in ((0, MUTED, 0), (55, BLUE, 0), (65, RED, 0), (78, RED, 0), (83, BLUE, 0), (99, MUTED, 0)):
        s.line(rx - 5, Y(mm), rx + 5, Y(mm), stroke=col, sw=1.5)
        s.text(rx - 10, Y(mm) + 4, str(mm), size=12, fill=col, anchor='end', weight='700')

    ly = Y(shown_h) + 34
    s.rect(ox, ly - 12, 22, 14, stroke=INK, sw=1.4, dash='5 3')
    s.text(ox + 30, ly, 'obszar strony widoczny w oknie przy każdym położeniu kartki', size=12.5)
    s.rect(ox + 420, ly - 12, 22, 14, fill='#E9EEF6', stroke=INK, sw=1)
    s.text(ox + 450, ly, 'tu musi zmieścić się zawartość (1 mm odstępu)', size=12.5)
    s.text(ox, ly + 26, 'Wszystko, co wyjdzie poza wypełniony prostokąt, jest błędem.', size=12.5, fill=MUTED)
    s.save('strona-okna.svg')


# ============================================================ 3. luz kartki
def play():
    S = 2.05
    pw, ph = ENV_W * S, ENV_H * S
    gap, ox, oy = 46, 40, 96
    s = Svg(int(2 * pw + gap + 2 * ox), int(ph + oy + 118), 'Luz kartki w kopercie i obszar widoczny zawsze')
    s.text(ox, 32, 'Dlaczego sprawdzany obszar jest mniejszy od okienka', size=19, weight='700')
    s.text(ox, 54, 'Złożona kartka (210 × 99 mm) jest mniejsza od koperty (229 × 114 mm), więc przesuwa się o 19 mm w poziomie i 15 mm w pionie.', size=13, fill=MUTED)

    for n, (dx, dy, caption) in enumerate(((0, 0, 'kartka dosunięta w lewo i do góry'), (19, 15, 'kartka dosunięta w prawo i w dół'))):
        px = ox + n * (pw + gap)
        X = lambda mm: px + mm * S
        Y = lambda mm: oy + mm * S
        s.text(px, oy - 12, caption, size=14, weight='700')
        s.rect(X(0), Y(0), pw, ph, fill=KRAFT, stroke=KRAFT_D, sw=2, rx=5)
        s.rect(X(dx), Y(dy), SHEET_W * S, SHEET_H * S, fill=PAPER, stroke=LINE, sw=1)
        s.text(X(dx) + 8, Y(dy) + 18, 'kartka', size=11, fill=MUTED)
        # treść kartki pod okienkami: obszary „zawsze widoczne” przesuwają się razem z kartką
        for (ax, ay, aw, ah), col, fill in ((SND_AREA, RED, RED_L), (REC_AREA, BLUE, BLUE_L)):
            s.rect(X(dx + ax), Y(dy + ay), aw * S, ah * S, fill=fill, stroke=col, sw=1.2, dash='5 3')
        label(s, X(dx + 29.5), Y(dy + 65.5), 48 * S, 12 * S)
        for i, wmm in enumerate((30, 44, 26)):
            s.rect(X(dx + 123), Y(dy + 59 + i * 4.6), wmm * S, 4.2, fill=INK, rx=1.5)
        # okienka koperty
        for (wx, wy, ww, wh), col in ((SND_WIN, RED), (REC_WIN, BLUE)):
            s.rect(X(wx), Y(wy), ww * S, wh * S, stroke=col, sw=3, rx=4)
        if n == 1:
            s.line(X(0.8), Y(64), X(18.2), Y(64), stroke=INK, arrows=True)
            s.text(X(9.5), Y(64) - 7, '19 mm', size=11.5, fill=INK, anchor='middle', weight='700')
            s.line(X(124), Y(0.8), X(124), Y(14.2), stroke=INK, arrows=True)
            s.text(X(124) + 10, Y(7.5) + 4, '15 mm', size=11.5, fill=INK, weight='700')

    ly = oy + ph + 34
    s.rect(ox, ly - 13, 26, 16, stroke=BLUE, sw=3, rx=3)
    s.text(ox + 36, ly, 'okienko koperty (nie rusza się)', size=13)
    s.rect(ox + 300, ly - 13, 26, 16, fill=BLUE_L, stroke=BLUE, sw=1.2, dash='5 3')
    s.text(ox + 336, ly, 'ten sam fragment strony w obu położeniach: zawsze trafia w okienko', size=13)
    s.text(ox, ly + 30, 'Adres i nalepka muszą leżeć w tym fragmencie. Wtedy widać je bez względu na to, jak kartka ułoży się w kopercie.', size=13, fill=MUTED)
    s.save('luz-kartki.svg')


# ============================================================ 4. nalepka a okno
def label_vs_window():
    S, ox, oy = 5.0, 50, 110
    s = Svg(980, 520, 'Rozmiar nalepki R a okno nadawcy')
    s.text(ox, 34, 'Okno nadawcy koperty C65: jaka nalepka R się mieści', size=19, weight='700')
    s.text(ox, 56, 'wszystkie prostokąty w tej samej skali, wymiary w milimetrach', size=13, fill=MUTED)

    # lewa strona: okno i to, co z niego zostaje
    X = lambda mm: ox + mm * S
    Y = lambda mm: oy + mm * S
    s.text(ox, oy - 14, 'Okno i obszar, który widać zawsze', size=14, weight='700')
    s.rect(X(0), Y(0), 70 * S, 30 * S, fill=KRAFT, stroke=KRAFT_D, sw=2.5, rx=5)
    s.text(X(70) - 8, Y(30) - 10, 'okienko koperty 70 × 30', size=12.5, fill='#7A6537', anchor='end')
    s.rect(X(0), Y(0), 51 * S, 15 * S, fill=PAPER, stroke=RED, sw=1.8, dash='7 4')
    s.rect(X(1), Y(1), 49 * S, 13 * S, fill=GREEN_L, stroke=GREEN, sw=1.8)
    s.text(X(25.5), Y(7.5) - 2, 'miejsce na nalepkę', size=13, fill=GREEN, anchor='middle', weight='700')
    s.text(X(25.5), Y(7.5) + 15, '49 × 13', size=13, fill=GREEN, anchor='middle')
    s.dim_h(X(0), X(51), Y(15) + 22, 'widoczne zawsze: 51', color=RED, above=False)
    s.dim_v(X(51) + 18, Y(0), Y(15), '15', color=RED, left=False)
    s.text(ox, Y(30) + 30, 'Luz kartki (19 × 15 mm) zabiera prawą i dolną część okienka,', size=12.5, fill=MUTED)
    s.text(ox, Y(30) + 48, 'a 1 mm odstępu od krawędzi zostawia 49 × 13 mm.', size=12.5, fill=MUTED)

    # prawa strona: nalepki
    rx = 470
    s.text(rx, oy - 14, 'Nalepki', size=14, weight='700')
    label(s, rx, oy, 65 * S, 25 * S)
    s.rect(rx, oy, 65 * S, 25 * S, stroke=RED, sw=2.5)
    s.text(rx + 65 * S + 14, oy + 22, '65 × 25 mm', size=14, weight='700', fill=RED)
    s.text(rx + 65 * S + 14, oy + 42, 'rozmiar domyślny', size=12.5, fill=MUTED)
    s.text(rx + 65 * S + 14, oy + 62, 'nie mieści się', size=12.5, fill=RED)
    s.text(rx + 65 * S + 14, oy + 80, 'LABEL_TOO_LARGE', size=11.5, fill=RED)

    y2 = oy + 25 * S + 46
    label(s, rx, y2, 48 * S, 12 * S)
    s.rect(rx, y2, 48 * S, 12 * S, stroke=GREEN, sw=2.5)
    s.text(rx + 48 * S + 14, y2 + 18, '48 × 12 mm', size=14, weight='700', fill=GREEN)
    s.text(rx + 48 * S + 14, y2 + 38, 'mieści się', size=12.5, fill=GREEN)
    s.text(rx + 48 * S + 14, y2 + 56, 'kreska kodu 0,21 mm przy 600 DPI', size=12.5, fill=MUTED)

    s.rect(ox, 430, 880, 62, fill='#FFF6DD', stroke='#F1DC9A', rx=6)
    s.text(ox + 16, 455, 'Mniejsza nalepka ma węższe kreski kodu (0,21 mm zamiast 0,25 mm).', size=13, fill='#7A5200', weight='700')
    s.text(ox + 16, 476, 'Przed masowym drukiem trzeba sprawdzić skanerem, czy kod z wydruku się czyta.', size=13, fill='#7A5200')
    s.save('nalepka-a-okno.svg')


# ============================================================ 5. budowa nalepki
def label_anatomy():
    S, ox, oy = 9.0, 290, 130
    w, h = 65 * S, 25 * S
    s = Svg(int(w + ox + 120), int(h + oy + 150), 'Budowa nalepki R')
    s.text(40, 34, 'Budowa nalepki R', size=19, weight='700')
    s.text(40, 56, 'schemat: kreski są umowne, proporcje jak w generatorze', size=13, fill=MUTED)

    s.rect(ox, oy, w, h, fill=PAPER, stroke=LINE, sw=1.2, extra='filter="url(#sh)"')
    pad = h * 0.04
    inner_h = h - 2 * pad
    text_h = inner_h * 0.2
    bar_h = inner_h * 0.77
    r_w = (w - 2 * pad) * 0.25
    bx = ox + pad + r_w + 18
    bw = w - pad - (bx - ox) - 34
    s.text(ox + pad + 2, oy + pad + bar_h, 'R', size=r_w / 0.74, fill=RED, weight='700')
    bars(s, bx + 26, oy + pad, bw - 26, bar_h, n=74)
    s.text(ox + w / 2, oy + h - pad - text_h * 0.22, '(00)75900773 1 51200062 1', size=text_h * 0.82, anchor='middle')

    # ciche strefy
    for qx in (bx, ox + w - pad - 34):
        s.rect(qx, oy + pad, 26 if qx == bx else 34, bar_h, fill=GREEN_L, stroke=GREEN, sw=1, dash='4 3')

    def callout(x1, y1, x2, y2, lines, color, anchor='start'):
        s.line(x1, y1, x2, y2, stroke=color, sw=1.4)
        s.raw(f'<circle cx="{x1:.1f}" cy="{y1:.1f}" r="3.5" fill="{color}"/>')
        for i, t in enumerate(lines):
            s.text(x2 + (10 if anchor == 'start' else -10), y2 + 4 + i * 17, t, size=12.5, fill=color if i == 0 else INK, anchor=anchor, weight='700' if i == 0 else 'normal')

    callout(ox + pad + r_w * 0.3, oy + pad + bar_h * 0.45, ox - 24, oy + 10, ['Litera R', 'czerwień #D50000', 'wysokość kodu, do 25% szerokości'], RED, 'end')
    callout(bx + bw * 0.55, oy + pad + 8, ox + w * 0.5, oy - 56, ['Kod kreskowy', 'krajowy: GS1-128, zagraniczny: Code 128', 'kreska = pełna liczba pikseli'], INK)
    callout(bx + 13, oy + pad + bar_h * 0.8, ox - 24, oy + h - 30, ['Cicha strefa', 'po 10 modułów z obu stron kodu'], GREEN, 'end')
    callout(ox + w * 0.72, oy + h - pad - text_h * 0.45, ox + w * 0.5, oy + h + 34, ['Numer w zapisie czytelnym', '20% wysokości, na całą szerokość'], BLUE)

    s.dim_h(ox, ox + w, oy + h + 96, 'szerokość: domyślnie 65 mm (do okna nadawcy C65 najwyżej 49 mm)', above=False)
    s.dim_v(ox + w + 30, oy, oy + h, 'wysokość: 25 mm (do okna: 13 mm)', left=False)
    s.save('nalepka-budowa.svg')


envelope()
page()
play()
label_vs_window()
label_anatomy()
