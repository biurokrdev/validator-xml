/**
 * Kalibracja interlinii mnożnikowej Word ↔ CSS (PG-09) — lustro backendowego
 * `WordLineSpacing.cs` (D2ViewerEditor.Infrastructure/Conversion). Tabele
 * współczynników MUSZĄ pozostać zsynchronizowane.
 *
 * W Wordzie „Wielokrotność N" = N × pojedynczy odstęp, a pojedynczy odstęp
 * wynika z metryk fontu (ascent+descent+lineGap ≈ 1,15–1,33 em). W CSS
 * bezjednostkowe line-height to dokładnie N × font-size. Reader emituje więc
 * wartość skalibrowaną (N × współczynnik fontu) + marker `--w-line-tw`
 * z oryginałem w 240-tych częściach linii; writer odtwarza w:line z markera.
 * GUI przy ustawianiu interlinii robi to samo, a przy odczycie (dialog akapitu)
 * preferuje marker — dzięki temu użytkownik zawsze widzi mnożnik Worda.
 */

/** Word wyraża mnożnik interlinii w 240-tych częściach linii. */
export const WORD_LINE_UNITS_PER_SINGLE = 240;

/** Fallback dla krojów spoza tabeli (np. firmowe) — środek zakresu metryk. */
export const DEFAULT_SINGLE_FACTOR = 1.2;

// (ascent+descent+lineGap)/unitsPerEm popularnych krojów Office/Windows.
const SINGLE_FACTORS: Record<string, number> = {
  'calibri': 1.221,
  'calibri light': 1.221,
  'cambria': 1.171,
  'arial': 1.15,
  'helvetica': 1.15,
  'times new roman': 1.149,
  'courier new': 1.133,
  'verdana': 1.215,
  'tahoma': 1.207,
  'segoe ui': 1.33,
  'georgia': 1.136,
};

/** Współczynnik pojedynczego odstępu dla kroju (pierwszy font z listy CSS). */
export function wordSingleFactor(fontFamily: string | null | undefined): number {
  if (!fontFamily) return DEFAULT_SINGLE_FACTOR;
  const first = fontFamily.split(',')[0]?.trim().replace(/^['"]|['"]$/g, '').trim().toLowerCase();
  return (first && SINGLE_FACTORS[first]) || DEFAULT_SINGLE_FACTOR;
}

/**
 * Ustawia na bloku interlinię mnożnikową Worda: skalibrowane line-height
 * (mnożnik × współczynnik fontu bloku) + marker --w-line-tw dla round-tripu.
 */
export function applyWordLineSpacing(el: HTMLElement, multiple: number): void {
  const factor = wordSingleFactor(getComputedStyle(el).fontFamily);
  const calibrated = Math.round(multiple * factor * 1000) / 1000;
  el.style.lineHeight = String(calibrated);
  el.style.setProperty('--w-line-tw', String(Math.round(multiple * WORD_LINE_UNITS_PER_SINGLE)));
}

/**
 * Ustawia stałą interlinię w pt (atLeast/exactly). Usuwa marker mnożnika, żeby
 * nie został przeterminowany; atLeast dostaje marker reguły (bez niego writer
 * zapisywał exact, który przycina w Wordzie tekst wyższy niż linia).
 */
export function applyExactLineSpacing(el: HTMLElement, points: number, atLeast: boolean): void {
  // PG-10: atLeast = „co najmniej" — linia rośnie, gdy treść wyższa (Word nie przycina).
  // Ten sam kontrakt co reader: max(pt, pojedynczy odstęp fontu z .page). Writer parsuje
  // pt z wnętrza max(...).
  el.style.lineHeight = atLeast
    ? `max(${points}pt, var(--w-line-single, 1.2em))`
    : `${points}pt`;
  el.style.removeProperty('--w-line-tw');
  if (atLeast) {
    el.style.setProperty('--w-line-rule', 'atLeast');
  } else {
    el.style.removeProperty('--w-line-rule');
  }
}

/**
 * Odczytuje mnożnik Worda z elementu: preferuje marker --w-line-tw (własny lub
 * odziedziczony — custom properties dziedziczą, a domyślna interlinia dokumentu
 * niesie marker na `.editor-content`). Bez markera dzieli wyliczone line-height
 * przez font-size (starsza treść bez kalibracji = surowy mnożnik Worda).
 * Zwraca null dla line-height w pt (exact/atLeast) i `normal`.
 */
export function readWordLineMultiple(el: HTMLElement): number | null {
  const style = getComputedStyle(el);
  const inline = el.style.lineHeight;
  if (inline.endsWith('pt')) return null;
  // atLeast (PG-10): max(Xpt, single) — to nie jest mnożnik.
  if (inline.startsWith('max(')) return null;

  const marker = parseFloat(style.getPropertyValue('--w-line-tw'));
  if (Number.isFinite(marker) && marker > 0) {
    return Math.round((marker / WORD_LINE_UNITS_PER_SINGLE) * 100) / 100;
  }

  const lineHeight = style.lineHeight;
  if (lineHeight === 'normal') return null;
  if (!lineHeight.endsWith('px')) {
    // Bezjednostkowa wartość wprost (np. jsdom / Firefox) — to już mnożnik.
    const n = parseFloat(lineHeight);
    return Number.isFinite(n) && n > 0 ? Math.round(n * 100) / 100 : null;
  }
  const lhPx = parseFloat(lineHeight);
  const fontPx = parseFloat(style.fontSize);
  if (!Number.isFinite(lhPx) || !Number.isFinite(fontPx) || fontPx <= 0) return null;
  return Math.round((lhPx / fontPx) * 100) / 100;
}
