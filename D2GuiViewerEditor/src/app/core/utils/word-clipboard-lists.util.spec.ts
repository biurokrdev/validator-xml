import { convertWordListParagraphs } from './word-clipboard-lists.util';

const NBSP6 = '&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp; ';
const STYLE =
  '<style><!-- @list l0 {mso-list-id:1;} @list l0:level1 {mso-level-tab-stop:none; text-indent:-18.0pt;}' +
  ' @list l1:level1 {mso-level-number-format:bullet; mso-level-text:bullet-glyph; font-family:Symbol;}' +
  ' @list l1:level2 {mso-level-number-format:bullet; mso-level-text:o; font-family:"Courier New";}' +
  ' @list l2:level1 {mso-level-number-format:bullet; mso-level-text:bullet-glyph; font-family:Wingdings;}' +
  ' @list l3:level1 {mso-level-number-format:alpha-lower; mso-level-text:"%1\\)";} --></style>';

function wordItem(list: string, markerHtml: string, text: string, font = ''): string {
  const fontStyle = font ? `font-family:${font};mso-fareast-font-family:${font}` : 'mso-bidi-font-family:Calibri';
  return `<p class=MsoListParagraphCxSpMiddle style='text-indent:-18.0pt;mso-list:${list}'>` +
    `<![if !supportLists]><span style='${fontStyle}'><span style='mso-list:Ignore'>${markerHtml}` +
    `<span style='font:7.0pt "Times New Roman"'>${NBSP6}</span></span></span><![endif]>${text}<o:p></o:p></p>`;
}

function convert(bodyHtml: string): { root: DocumentFragment; trusted: Set<Element> } {
  const raw = `<html><head>${STYLE}</head><body><!--StartFragment-->${bodyHtml}<!--EndFragment--></body></html>`;
  const tpl = document.createElement('template');
  tpl.innerHTML = raw;
  tpl.content.querySelectorAll('style').forEach(s => s.remove());
  let seq = 0;
  const trusted = convertWordListParagraphs(tpl.content, raw, () => `w-${++seq}`);
  return { root: tpl.content, trusted };
}

describe('convertWordListParagraphs — listy ze schowka MS Word', () => {
  it('lista punktowana: akapity mso-list → jedno <ul>, znacznik „·" znika z treści', () => {
    const { root } = convert(
      '<p class=MsoNormal>Wstep</p>' +
      wordItem('l1 level1 lfo1', '·', 'Punkt pierwszy', 'Symbol') +
      wordItem('l1 level1 lfo1', '·', 'Punkt <b>drugi</b>', 'Symbol') +
      '<p class=MsoNormal>Koniec</p>');

    const lists = root.querySelectorAll('ul');
    expect(lists.length).toBe(1);
    const items = Array.from(lists[0].children);
    expect(items.map(li => li.tagName)).toEqual(['LI', 'LI']);
    expect(items[0].textContent).toBe('Punkt pierwszy');
    expect(items[1].querySelector('b')!.textContent).toBe('drugi');
    expect(root.textContent).not.toContain('·');
    expect(lists[0].hasAttribute('data-num-id')).toBe(false);
    expect(lists[0].previousElementSibling!.textContent).toBe('Wstep');
    expect(lists[0].nextElementSibling!.textContent).toBe('Koniec');
    expect(root.querySelector('[style*="mso-list"]')).toBeNull();
  });

  it('poziomy: level2 zagnieżdża listę w poprzednim punkcie i wraca na level1', () => {
    const { root } = convert(
      wordItem('l1 level1 lfo1', '·', 'A', 'Symbol') +
      wordItem('l1 level2 lfo1', 'o', 'A1', '"Courier New"') +
      wordItem('l1 level2 lfo1', 'o', 'A2', '"Courier New"') +
      wordItem('l1 level1 lfo1', '·', 'B', 'Symbol'));

    const topLists = Array.from(root.children).filter(e => e.tagName === 'UL');
    expect(topLists.length).toBe(1);
    const top = topLists[0];
    expect(Array.from(top.children).map(li => li.firstChild!.textContent)).toEqual(['A', 'B']);
    const nested = Array.from(top.children[0].children).find(e => e.tagName === 'UL')!;
    expect(Array.from(nested.children).map(li => li.textContent)).toEqual(['A1', 'A2']);
  });

  it('lista numerowana „1." → goły <ol>; numer nie zostaje w tekście', () => {
    const { root } = convert(
      wordItem('l0 level1 lfo2', '1.', 'Krok jeden') + wordItem('l0 level1 lfo2', '2.', 'Krok dwa'));

    const ol = root.querySelector('ol')!;
    expect(Array.from(ol.children).map(li => li.textContent)).toEqual(['Krok jeden', 'Krok dwa']);
    expect(ol.hasAttribute('data-num-id')).toBe(false);
  });

  it('numeracja literowa „a)" → kontrakt data-* (format i szablon etykiety)', () => {
    const { root, trusted } = convert(
      wordItem('l3 level1 lfo4', 'a)', 'Wariant') + wordItem('l3 level1 lfo4', 'b)', 'Inny'));

    const ol = root.querySelector('ol')!;
    expect(ol.getAttribute('data-num-fmt')).toBe('lowerLetter');
    expect(ol.getAttribute('data-lvl-text')).toBe('%1)');
    expect(ol.getAttribute('data-num-id')).toBe('w-1');
    expect(ol.children.length).toBe(2);
    expect(trusted.has(ol)).toBe(true);
  });

  it('punktor-pole wyboru (Wingdings 0xA8) → lista pól wyboru edytora z markerem ☐', () => {
    const { root, trusted } = convert(
      wordItem('l2 level1 lfo3', '¨', 'Zadanie', 'Wingdings') +
      wordItem('l2 level1 lfo3', '¨', 'Drugie', 'Wingdings'));

    const ul = root.querySelector('ul')!;
    expect(ul.getAttribute('data-num-fmt')).toBe('bullet');
    expect(ul.getAttribute('data-lvl-text')).toBe(String.fromCharCode(0xf0a8));
    expect(ul.getAttribute('data-bullet-font')).toBe('Wingdings');
    expect(ul.style.listStyleType).toBe('none');
    const markers = Array.from(ul.querySelectorAll('li > span.list-marker'));
    expect(markers.map(m => m.textContent)).toEqual(['☐', '☐']);
    expect(markers.every(m => trusted.has(m))).toBe(true);
    expect(ul.children[0].textContent).toBe('☐Zadanie');
  });

  it('dwie różne listy jedna po drugiej nie sklejają się; akapit między listami przerywa ciąg', () => {
    const { root } = convert(
      wordItem('l1 level1 lfo1', '·', 'Punkt', 'Symbol') +
      wordItem('l0 level1 lfo2', '1.', 'Krok') +
      '<p class=MsoNormal>Przerwa</p>' +
      wordItem('l0 level1 lfo2', '2.', 'Krok dalej'));

    expect(Array.from(root.children).map(e => e.tagName)).toEqual(['UL', 'OL', 'P', 'OL']);
  });

  it('HTML bez list Worda zostaje nietknięty', () => {
    const { root, trusted } = convert('<p>Zwykły</p><ul><li>już lista</li></ul>');

    expect(trusted.size).toBe(0);
    expect(root.querySelectorAll('ul').length).toBe(1);
    expect(root.querySelector('p')!.textContent).toBe('Zwykły');
  });
});
