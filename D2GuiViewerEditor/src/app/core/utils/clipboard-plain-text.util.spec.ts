import { clipboardPlainText } from './clipboard-plain-text.util';

function fromHtml(html: string): HTMLElement {
  const div = document.createElement('div');
  div.innerHTML = html;
  return div;
}

describe('clipboardPlainText', () => {
  it('akapity = pojedyncze linie, bez pustego wiersza między nimi', () => {
    expect(clipboardPlainText(fromHtml('<p>Pierwsza</p><p>Druga</p><p>Trzecia</p>')))
      .toBe('Pierwsza\nDruga\nTrzecia');
  });

  it('ignoruje formatowanie źródła HTML między blokami', () => {
    expect(clipboardPlainText(fromHtml('<p>\n<span>A</span>\n</p>\n<p>\n<span>B</span>\n</p>\n')))
      .toBe('A\nB');
  });

  it('pusty akapit (placeholder <br>) = jedna pusta linia', () => {
    expect(clipboardPlainText(fromHtml('<p>A</p><p><br></p><p>B</p>'))).toBe('A\n\nB');
  });

  it('końcowy <br> akapitu nie dokłada linii, środkowy łamie wiersz', () => {
    expect(clipboardPlainText(fromHtml('<p>A<br>B<span><br></span></p><p>C</p>'))).toBe('A\nB\nC');
  });

  it('elementy listy w osobnych liniach, marker zostaje', () => {
    const html = '<ul><li><span class="list-marker" contenteditable="false">•</span>jeden</li><li>dwa</li></ul><p>po</p>';
    expect(clipboardPlainText(fromHtml(html))).toBe('•jeden\ndwa\npo');
  });

  it('tabela: komórki rozdzielone tabulatorem, wiersze liniami', () => {
    const html = '<table><colgroup><col></colgroup><tbody><tr><td><p>a</p></td><td>b</td></tr><tr><td>c</td><td><p>d1</p><p>d2</p></td></tr></tbody></table>';
    expect(clipboardPlainText(fromHtml(html))).toBe('a\tb\nc\td1 d2');
  });

  it('tekst inline bez bloku, twarde spacje i znaki zerowej szerokości', () => {
    expect(clipboardPlainText(fromHtml('<span>a b​</span>'))).toBe('a b');
  });
});
