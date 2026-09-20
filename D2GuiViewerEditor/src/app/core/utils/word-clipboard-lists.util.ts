
import { synthesizeBulletMarker } from './list-label.util';

interface WordListRef {
  listKey: string;
  listId: string;
  level: number;
}

interface LevelDef {
  numberFormat: string | null;
}

function readWordListRef(el: Element): WordListRef | null {
  const style = el.getAttribute('style') ?? '';
  const m = /mso-list\s*:\s*l(\d+)\s+level(\d+)(?:\s+lfo(\d+))?/i.exec(style);
  if (!m) return null;
  return {
    listId: m[1],
    listKey: `l${m[1]}-lfo${m[3] ?? ''}`,
    level: Math.min(9, Math.max(1, parseInt(m[2], 10))),
  };
}

function readLevelDefs(rawHtml: string): Map<string, LevelDef> {
  const defs = new Map<string, LevelDef>();
  const re = /@list\s+l(\d+):level(\d+)\s*\{([^}]*)\}/gi;
  for (let m = re.exec(rawHtml); m; m = re.exec(rawHtml)) {
    const fmt = /mso-level-number-format\s*:\s*([^;]+)/i.exec(m[3]);
    defs.set(`${m[1]}:${m[2]}`, { numberFormat: fmt ? fmt[1].trim().toLowerCase() : null });
  }
  return defs;
}

const WORD_NUMBER_FORMATS: Record<string, string> = {
  'alpha-lower': 'lowerLetter',
  'alpha-upper': 'upperLetter',
  'roman-lower': 'lowerRoman',
  'roman-upper': 'upperRoman',
  'arabic-leading-zero': 'decimalZero',
  'decimal-leading-zero': 'decimalZero',
};

function isDefaultBullet(marker: string, font: string): boolean {
  const f = font.toLowerCase();
  if (marker === '•' || marker === '·' || marker === '◦' || marker === '▪') return true;
  const code = marker.codePointAt(0) ?? 0;
  const low = code >= 0xf000 && code <= 0xf0ff ? code & 0xff : code;
  if (f.includes('symbol') && low === 0xb7) return true;
  if (f.includes('courier') && marker === 'o') return true;
  if (f.includes('wingdings') && low === 0xa7) return true;
  return false;
}

function extractMarker(p: Element): { text: string; font: string } {
  const walker = p.ownerDocument.createTreeWalker(p, NodeFilter.SHOW_COMMENT);
  const comments: Node[] = [];
  for (let n = walker.nextNode(); n; n = walker.nextNode()) comments.push(n);
  comments.forEach(c => c.parentNode?.removeChild(c));

  const ignore = Array.from(p.querySelectorAll<HTMLElement>('span'))
    .find(s => /mso-list\s*:\s*ignore/i.test(s.getAttribute('style') ?? ''));
  if (!ignore) return { text: '', font: '' };

  const text = Array.from(ignore.childNodes)
    .filter(n => n.nodeType === Node.TEXT_NODE)
    .map(n => n.nodeValue ?? '')
    .join('')
    .replace(/\s/g, '');

  let wrapper: HTMLElement = ignore;
  let font = ignore.style.fontFamily;
  while (wrapper.parentElement && wrapper.parentElement !== p
    && wrapper.parentElement.tagName === 'SPAN'
    && wrapper.parentElement.textContent === ignore.textContent) {
    wrapper = wrapper.parentElement;
    font = font || wrapper.style.fontFamily;
  }
  wrapper.remove();
  return { text, font: font.replace(/["']/g, '').split(',')[0].trim() };
}

function lvlTextFromMarker(marker: string, level: number): string {
  const groups = marker.match(/[0-9]+|[A-Za-z]+/g) ?? [];
  if (groups.length === 0) return `%${level}.`;
  let index = 0;
  const first = groups.length === level ? 1 : level - groups.length + 1;
  return marker.replace(/[0-9]+|[A-Za-z]+/g, () => `%${Math.max(1, first + index++)}`);
}

interface ContainerSpec {
  tag: 'ul' | 'ol';
  signature: string;
  contract: Record<string, string> | null;
}

function containerSpec(
  ref: WordListRef, marker: { text: string; font: string }, def: LevelDef | undefined,
  numIdFor: (key: string) => string,
): ContainerSpec {
  const wordFmt = def?.numberFormat ?? null;
  const looksNumbered = /^[([]?(?:[0-9]+|[A-Za-z]{1,4})(?:[.)\]]|(?:\.[0-9]+)+\.?)$/.test(marker.text);
  const ordered = wordFmt ? wordFmt !== 'bullet' && wordFmt !== 'image' && wordFmt !== 'none' : looksNumbered;
  const ilvl = String(ref.level - 1);

  if (ordered) {
    const fmt = (wordFmt && WORD_NUMBER_FORMATS[wordFmt]) || 'decimal';
    const lvlText = marker.text ? lvlTextFromMarker(marker.text, ref.level) : `%${ref.level}.`;
    const plain = fmt === 'decimal' && lvlText === `%${ref.level}.`;
    return {
      tag: 'ol',
      signature: `ol|${ref.listKey}|${ref.level}|${fmt}|${lvlText}`,
      contract: plain ? null : {
        'data-num-id': numIdFor(ref.listKey),
        'data-ilvl': ilvl,
        'data-num-fmt': fmt,
        'data-lvl-text': lvlText,
      },
    };
  }

  if (!marker.text || isDefaultBullet(marker.text, marker.font)) {
    return { tag: 'ul', signature: `ul|${ref.listKey}|${ref.level}|default`, contract: null };
  }

  const symbolic = /wingdings|webdings|symbol/i.test(marker.font);
  const code = marker.text.codePointAt(0) ?? 0x2022;
  const lvlText = symbolic && code <= 0xff ? String.fromCharCode(0xf000 | code) : marker.text;
  const contract: Record<string, string> = {
    'data-num-id': numIdFor(`${ref.listKey}|${lvlText}`),
    'data-ilvl': ilvl,
    'data-num-fmt': 'bullet',
    'data-lvl-text': lvlText,
    'data-ind-left-tw': String(720 * ref.level),
    'data-ind-hanging-tw': '360',
    style: 'list-style-type:none;margin:0;padding-left:48px;--ind-hanging:24px;',
  };
  if (symbolic) contract['data-bullet-font'] = marker.font;
  return { tag: 'ul', signature: `ul|${ref.listKey}|${ref.level}|${lvlText}|${marker.font}`, contract };
}

export function convertWordListParagraphs(
  root: ParentNode, rawHtml: string, nextListId: () => string,
): Set<Element> {
  const candidates = Array.from(root.querySelectorAll<HTMLElement>('p, h1, h2, h3, h4, h5, h6, div'))
    .filter(el => readWordListRef(el) !== null);
  const trusted = new Set<Element>();
  if (candidates.length === 0) return trusted;

  const defs = readLevelDefs(rawHtml);
  const ids = new Map<string, string>();
  const numIdFor = (key: string) => {
    let id = ids.get(key);
    if (!id) { id = nextListId(); ids.set(key, id); }
    return id;
  };

  interface Frame { level: number; signature: string; list: HTMLElement; lastLi: HTMLElement | null }
  let stack: Frame[] = [];

  const makeList = (spec: ContainerSpec): HTMLElement => {
    const list = root.ownerDocument!.createElement(spec.tag);
    if (spec.contract) Object.entries(spec.contract).forEach(([k, v]) => list.setAttribute(k, v));
    trusted.add(list);
    return list;
  };

  for (const p of candidates) {
    if (!root.contains(p)) continue;
    const ref = readWordListRef(p)!;
    let cursor: Node | null = p.previousSibling;
    while (cursor && cursor.nodeType !== Node.ELEMENT_NODE) cursor = cursor.previousSibling;
    const rootList = stack[0]?.list ?? null;
    if (!rootList || cursor !== rootList) stack = [];

    const marker = extractMarker(p);
    const spec = containerSpec(ref, marker, defs.get(`${ref.listId}:${ref.level}`), numIdFor);

    while (stack.length > 0 && stack[stack.length - 1].level > ref.level) stack.pop();
    let top = stack[stack.length - 1];

    if (!top) {
      const list = makeList(spec);
      p.parentNode!.insertBefore(list, p);
      top = { level: ref.level, signature: spec.signature, list, lastLi: null };
      stack = [top];
    } else if (top.level < ref.level && top.lastLi) {
      const list = makeList(spec);
      top.lastLi.appendChild(list);
      top = { level: ref.level, signature: spec.signature, list, lastLi: null };
      stack.push(top);
    } else if (top.signature !== spec.signature) {
      const list = makeList(spec);
      top.list.parentNode!.insertBefore(list, top.list.nextSibling);
      top = { level: ref.level, signature: spec.signature, list, lastLi: null };
      stack[stack.length - 1] = top;
    }

    const li = root.ownerDocument!.createElement('li');
    while (p.firstChild) li.appendChild(p.firstChild);
    top.list.appendChild(li);
    top.lastLi = li;
    if (spec.contract?.['data-num-fmt'] === 'bullet') {
      const markerEl = synthesizeBulletMarker(top.list);
      if (markerEl) {
        li.insertBefore(markerEl, li.firstChild);
        trusted.add(markerEl);
      }
    }
    p.remove();
  }
  return trusted;
}
