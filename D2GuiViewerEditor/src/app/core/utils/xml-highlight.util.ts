export type XmlTokenKind = 'markup' | 'name' | 'attribute' | 'value' | 'text' | 'comment';

export interface XmlToken {
  kind: XmlTokenKind;
  text: string;
}

export const XML_HIGHLIGHT_LIMIT = 120_000;

export function tokenizeXml(xml: string): XmlToken[] {
  if (xml.length > XML_HIGHLIGHT_LIMIT) {
    return [{ kind: 'text', text: xml }];
  }

  const tokens: XmlToken[] = [];
  let index = 0;

  while (index < xml.length) {
    const tagStart = xml.indexOf('<', index);

    if (tagStart < 0) {
      push(tokens, 'text', xml.slice(index));
      break;
    }

    push(tokens, 'text', xml.slice(index, tagStart));

    if (xml.startsWith('<!--', tagStart)) {
      const commentEnd = xml.indexOf('-->', tagStart);
      const end = commentEnd < 0 ? xml.length : commentEnd + 3;
      push(tokens, 'comment', xml.slice(tagStart, end));
      index = end;
      continue;
    }

    const tagEnd = xml.indexOf('>', tagStart);

    if (tagEnd < 0) {
      push(tokens, 'markup', xml.slice(tagStart));
      break;
    }

    tokenizeTag(tokens, xml.slice(tagStart, tagEnd + 1));
    index = tagEnd + 1;
  }

  return tokens;
}

function tokenizeTag(tokens: XmlToken[], tag: string): void {
  const nameMatch = /^<[/?!]?\s*([^\s/>]+)/.exec(tag);

  if (!nameMatch) {
    push(tokens, 'markup', tag);
    return;
  }

  const nameEnd = nameMatch[0].length;
  push(tokens, 'markup', tag.slice(0, nameEnd - nameMatch[1].length));
  push(tokens, 'name', nameMatch[1]);

  const rest = tag.slice(nameEnd);
  const attributePattern = /([^\s=]+)(\s*=\s*)("[^"]*"|'[^']*')/g;
  let cursor = 0;
  let match: RegExpExecArray | null;

  while ((match = attributePattern.exec(rest)) !== null) {
    push(tokens, 'markup', rest.slice(cursor, match.index));
    push(tokens, 'attribute', match[1]);
    push(tokens, 'markup', match[2]);
    push(tokens, 'value', match[3]);
    cursor = match.index + match[0].length;
  }

  push(tokens, 'markup', rest.slice(cursor));
}

function push(tokens: XmlToken[], kind: XmlTokenKind, text: string): void {
  if (text.length === 0) {
    return;
  }

  const last = tokens[tokens.length - 1];

  if (last && last.kind === kind) {
    last.text += text;
    return;
  }

  tokens.push({ kind, text });
}
