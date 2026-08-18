
export interface ListContainerAttrs {
  ordered: boolean;
  numId: string;
  counterKey: string;
  level: number;
  fmt: string;
  lvlText: string | null;
  start: number;
  startOverride: number;
  isLegal: boolean;
  lvlRestart: number;
  suffix: 'tab' | 'space' | 'nothing';
  picBullet: boolean;
}

export function readListContainerAttrs(el: Element): ListContainerAttrs | null {
  const numId = el.getAttribute('data-num-id');
  if (!numId) return null;
  const abstractId = el.getAttribute('data-abstract-num-id');
  const intAttr = (name: string, fallback: number): number => {
    const v = parseInt(el.getAttribute(name) ?? '', 10);
    return Number.isFinite(v) ? v : fallback;
  };
  const suffixRaw = el.getAttribute('data-suffix');
  return {
    ordered: el.tagName.toLowerCase() === 'ol',
    numId,
    counterKey: abstractId !== null ? `abs:${abstractId}` : `num:${numId}`,
    level: Math.min(8, Math.max(0, intAttr('data-ilvl', 0))),
    fmt: el.getAttribute('data-num-fmt') ?? 'decimal',
    lvlText: el.getAttribute('data-lvl-text'),
    start: intAttr('data-start', 1),
    startOverride: intAttr('data-start-override', -1),
    isLegal: el.getAttribute('data-is-legal') === '1',
    lvlRestart: intAttr('data-lvl-restart', -1),
    suffix: suffixRaw === 'space' || suffixRaw === 'nothing' ? suffixRaw : 'tab',
    picBullet: el.getAttribute('data-pic-bullet') === '1',
  };
}

export function formatListNumber(fmt: string, n: number): string {
  if (n < 1) n = 1;
  switch (fmt) {
    case 'decimalZero':
      return n < 10 ? `0${n}` : String(n);
    case 'lowerLetter':
      return toWordLetter(n);
    case 'upperLetter':
      return toWordLetter(n).toUpperCase();
    case 'lowerRoman':
      return toRoman(n).toLowerCase();
    case 'upperRoman':
      return toRoman(n);
    default:
      return String(n);
  }
}

function toWordLetter(n: number): string {
  const repeat = Math.ceil(n / 26);
  const letter = String.fromCharCode(97 + ((n - 1) % 26));
  return letter.repeat(repeat);
}

function toRoman(n: number): string {
  const map: Array<[number, string]> = [
    [1000, 'M'], [900, 'CM'], [500, 'D'], [400, 'CD'],
    [100, 'C'], [90, 'XC'], [50, 'L'], [40, 'XL'],
    [10, 'X'], [9, 'IX'], [5, 'V'], [4, 'IV'], [1, 'I'],
  ];
  let result = '';
  for (const [value, symbol] of map) {
    while (n >= value) {
      result += symbol;
      n -= value;
    }
  }
  return result || 'I';
}

interface LevelSpec {
  fmt: string;
  start: number;
  lvlRestart: number;
}

export class ListLabelEngine {
  private readonly counters = new Map<string, Array<number | undefined>>();
  private readonly appliedOverrides = new Set<string>();
  private readonly specs = new Map<string, LevelSpec>();

  next(attrs: ListContainerAttrs): string | null {
    this.registerSpec(attrs);
    this.applyStartOverrideOnFirstUse(attrs);

    const row = this.countersFor(attrs.counterKey);
    const spec = this.specFor(attrs.counterKey, attrs.level);
    row[attrs.level] = row[attrs.level] === undefined ? spec.start : row[attrs.level]! + 1;

    for (let deeper = attrs.level + 1; deeper <= 8; deeper++) {
      if (row[deeper] === undefined) continue;
      const deeperRestart = this.specs.get(`${attrs.counterKey}|${deeper}`)?.lvlRestart ?? -1;
      if (deeperRestart === 0) continue;
      if (deeperRestart > 0 && attrs.level >= deeperRestart) continue;
      row[deeper] = undefined;
    }

    if (attrs.fmt === 'bullet' || attrs.fmt === 'none' || attrs.picBullet) return null;
    return this.buildLabel(attrs, row);
  }

  private buildLabel(attrs: ListContainerAttrs, row: Array<number | undefined>): string {
    const template =
      attrs.lvlText && attrs.lvlText.includes('%') ? attrs.lvlText : `%${attrs.level + 1}.`;
    return template.replace(/%(\d)/g, (_m, digit: string) => {
      const refLevel = parseInt(digit, 10) - 1;
      const refSpec = this.specFor(attrs.counterKey, refLevel);
      const value = row[refLevel] ?? refSpec.start;
      const fmt = attrs.isLegal ? 'decimal' : refLevel === attrs.level ? attrs.fmt : refSpec.fmt;
      return formatListNumber(fmt, value);
    });
  }

  private registerSpec(attrs: ListContainerAttrs): void {
    const key = `${attrs.counterKey}|${attrs.level}`;
    if (!this.specs.has(key)) {
      this.specs.set(key, { fmt: attrs.fmt, start: attrs.start, lvlRestart: attrs.lvlRestart });
    }
  }

  private specFor(counterKey: string, level: number): LevelSpec {
    return this.specs.get(`${counterKey}|${level}`) ?? { fmt: 'decimal', start: 1, lvlRestart: -1 };
  }

  private applyStartOverrideOnFirstUse(attrs: ListContainerAttrs): void {
    if (attrs.startOverride <= 0 || this.appliedOverrides.has(attrs.numId)) return;
    this.appliedOverrides.add(attrs.numId);
    const row = this.countersFor(attrs.counterKey);
    row[attrs.level] = attrs.startOverride - 1;
  }

  private countersFor(counterKey: string): Array<number | undefined> {
    let row = this.counters.get(counterKey);
    if (!row) {
      row = new Array<number | undefined>(9).fill(undefined);
      this.counters.set(counterKey, row);
    }
    return row;
  }
}

export function applyListLabels(roots: Iterable<HTMLElement>): void {
  const engine = new ListLabelEngine();
  for (const root of roots) {
    const items = root.querySelectorAll<HTMLLIElement>(
      'ol[data-num-id] > li, ul[data-num-id] > li',
    );
    items.forEach(li => {
      const container = li.parentElement;
      const attrs = container ? readListContainerAttrs(container) : null;
      if (!attrs) return;
      const label = engine.next(attrs);
      if (label !== null) {
        li.setAttribute('data-list-label', label);
        if (attrs.suffix !== 'tab') li.setAttribute('data-list-suffix', attrs.suffix);
        else li.removeAttribute('data-list-suffix');
      } else {
        li.removeAttribute('data-list-label');
        li.removeAttribute('data-list-suffix');
      }
    });
  }
}

export function stripListLabelAttributes(root: Element): void {
  root.querySelectorAll('[data-list-label], [data-list-suffix]').forEach(el => {
    el.removeAttribute('data-list-label');
    el.removeAttribute('data-list-suffix');
  });
}

export function ensureBulletMarkers(roots: Iterable<HTMLElement>): void {
  for (const root of roots) {
    root.querySelectorAll<HTMLElement>('ul[data-num-id], ol[data-num-id]').forEach(container => {
      const items = Array.from(container.children).filter(
        (c): c is HTMLLIElement => c.tagName === 'LI',
      );
      const template = items
        .map(li => li.querySelector<HTMLElement>(':scope > span.list-marker'))
        .find(span => span !== null)
        ?? synthesizeBulletMarker(container);
      if (!template) return;
      for (const li of items) {
        if (li.querySelector(':scope > span.list-marker')) continue;
        li.insertBefore(template.cloneNode(true), li.firstChild);
      }
    });
  }
}

export function synthesizeBulletMarker(container: Element): HTMLElement | null {
  const attrs = readListContainerAttrs(container);
  if (!attrs || attrs.fmt !== 'bullet' || attrs.picBullet || !attrs.lvlText) return null;
  const span = document.createElement('span');
  span.className = 'list-marker';
  span.setAttribute('contenteditable', 'false');
  span.setAttribute('style', 'display:inline-block;min-width:1.2em;margin-right:0.4em;');
  span.textContent = bulletGlyphFromContract(attrs.lvlText, container.getAttribute('data-bullet-font'));
  return span;
}

const WINGDINGS_GLYPHS: Record<number, string> = {
  0x6c: '●', 0x6e: '■', 0x6f: '□', 0x71: '❑', 0x72: '❒', 0x75: '◆',
  0xa7: '■', 0xa8: '☐', 0xd8: '❖',
  0xfb: '✗', 0xfc: '✔', 0xfd: '☒', 0xfe: '☑',
};

export function bulletGlyphFromContract(lvlText: string, font: string | null): string {
  const code = lvlText.codePointAt(0) ?? 0x2022;
  const isPua = code >= 0xf000 && code <= 0xf0ff;
  const low = isPua ? code & 0xff : code;
  const f = (font ?? '').toLowerCase();
  if (isPua || f.includes('wingdings') || f.includes('webdings') || f === 'symbol') {
    return WINGDINGS_GLYPHS[low] ?? '•';
  }
  return lvlText;
}
