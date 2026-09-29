
const BLOCK_TAGS = new Set([
  'P', 'DIV', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'LI', 'UL', 'OL',
  'TABLE', 'THEAD', 'TBODY', 'TFOOT', 'BLOCKQUOTE', 'PRE',
  'SECTION', 'ARTICLE', 'HEADER', 'FOOTER',
]);

const PARAGRAPH_TAGS = new Set(['P', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'LI', 'PRE']);

const SKIP_TAGS = new Set(['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE', 'COLGROUP', 'COL']);

export function clipboardPlainText(root: Node): string {
  return serialize(root)
    .replace(/[​﻿]/g, '')
    .replace(/ /g, ' ')
    .replace(/\n+$/, '');
}

function serialize(root: Node): string {
  let out = '';
  const breakLine = () => {
    if (out && !out.endsWith('\n')) out += '\n';
  };

  const walk = (node: Node): void => {
    for (const child of Array.from(node.childNodes)) {
      if (child.nodeType === Node.TEXT_NODE) {
        const text = (child as Text).data;
        if (text.includes('\n') && text.trim() === '') continue;
        out += text;
        continue;
      }
      if (!(child instanceof Element) || SKIP_TAGS.has(child.tagName)) continue;

      const tag = child.tagName;
      if (tag === 'BR') {
        if (!isTrailingBr(child)) out += '\n';
        continue;
      }
      if (tag === 'TR') {
        breakLine();
        out += Array.from(child.children)
          .filter(c => c.tagName === 'TD' || c.tagName === 'TH')
          .map(cell => serialize(cell).replace(/\n+$/, '').replace(/\n/g, ' '))
          .join('\t');
        breakLine();
        continue;
      }
      if (BLOCK_TAGS.has(tag)) {
        breakLine();
        const start = out.length;
        walk(child);
        if (out.length === start && PARAGRAPH_TAGS.has(tag)) out += '\n';
        breakLine();
        continue;
      }
      walk(child);
    }
  };

  walk(root);
  return out;
}

function isTrailingBr(br: Element): boolean {
  let node: Node = br;
  while (true) {
    for (let next = node.nextSibling; next; next = next.nextSibling) {
      if (next.nodeType === Node.TEXT_NODE && (next.textContent ?? '').replace(/[​﻿]/g, '').trim() === '') continue;
      return false;
    }
    const parent = node.parentNode;
    if (!parent || !(parent instanceof Element) || BLOCK_TAGS.has(parent.tagName) || parent.tagName === 'TD' || parent.tagName === 'TH') {
      return true;
    }
    node = parent;
  }
}
