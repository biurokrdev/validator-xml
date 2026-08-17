
export const WORD_LINE_UNITS_PER_SINGLE = 240;

export const DEFAULT_SINGLE_FACTOR = 1.2;

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

export function wordSingleFactor(fontFamily: string | null | undefined): number {
  if (!fontFamily) return DEFAULT_SINGLE_FACTOR;
  const first = fontFamily.split(',')[0]?.trim().replace(/^['"]|['"]$/g, '').trim().toLowerCase();
  return (first && SINGLE_FACTORS[first]) || DEFAULT_SINGLE_FACTOR;
}

export function applyWordLineSpacing(el: HTMLElement, multiple: number): void {
  const factor = wordSingleFactor(getComputedStyle(el).fontFamily);
  const calibrated = Math.round(multiple * factor * 1000) / 1000;
  el.style.lineHeight = String(calibrated);
  el.style.setProperty('--w-line-tw', String(Math.round(multiple * WORD_LINE_UNITS_PER_SINGLE)));
}

export function applyExactLineSpacing(el: HTMLElement, points: number, atLeast: boolean): void {
  
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

export function readWordLineMultiple(el: HTMLElement): number | null {
  const style = getComputedStyle(el);
  const inline = el.style.lineHeight;
  if (inline.endsWith('pt')) return null;
  
  if (inline.startsWith('max(')) return null;

  const marker = parseFloat(style.getPropertyValue('--w-line-tw'));
  if (Number.isFinite(marker) && marker > 0) {
    return Math.round((marker / WORD_LINE_UNITS_PER_SINGLE) * 100) / 100;
  }

  const lineHeight = style.lineHeight;
  if (lineHeight === 'normal') return null;
  if (!lineHeight.endsWith('px')) {
    
    const n = parseFloat(lineHeight);
    return Number.isFinite(n) && n > 0 ? Math.round(n * 100) / 100 : null;
  }
  const lhPx = parseFloat(lineHeight);
  const fontPx = parseFloat(style.fontSize);
  if (!Number.isFinite(lhPx) || !Number.isFinite(fontPx) || fontPx <= 0) return null;
  return Math.round((lhPx / fontPx) * 100) / 100;
}
