import { XML_FORMAT_LIMIT, collectHiddenLines, toFormattedLines, toRawLines } from './xml-format.util';

describe('xml-format.util', () => {
  it('numeruje wiersze źródła zgodnie z podziałem na linie', () => {
    const lines = toRawLines('<a>\n  <b/>\n</a>');

    expect(lines.map((line) => line.number)).toEqual([1, 2, 3]);
    expect(lines[1].text).toBe('  <b/>');
  });

  it('formatuje jeden znacznik na wiersz z wcięciem wg zagnieżdżenia', () => {
    const lines = toFormattedLines('<w:p><w:r><w:t>Tekst</w:t></w:r></w:p>');

    expect(lines.map((line) => line.text)).toEqual([
      '<w:p>',
      '<w:r>',
      '<w:t>',
      'Tekst',
      '</w:t>',
      '</w:r>',
      '</w:p>',
    ]);
    expect(lines.map((line) => line.depth)).toEqual([0, 1, 2, 3, 2, 1, 0]);
  });

  it('nie kończy znacznika na znaku > wewnątrz wartości atrybutu', () => {
    const lines = toFormattedLines('<w:dataBinding w:xpath="/a[b>1]"/><w:t>x</w:t>');

    expect(lines[0].text).toBe('<w:dataBinding w:xpath="/a[b>1]"/>');
    expect(lines[1].text).toBe('<w:t>');
  });

  it('wyznacza zakres zwijania dla elementu z zawartością', () => {
    const lines = toFormattedLines('<a><b><c/></b></a>');
    const root = lines[0];

    expect(root.foldable).toBe(true);
    expect(root.foldEnd).toBe(lines[lines.length - 1].number);
    expect(collectHiddenLines(lines, new Set([root.number])).size).toBe(lines.length - 1);
  });

  it('nie oznacza jako zwijalnego elementu bez zawartości', () => {
    const lines = toFormattedLines('<a></a>');

    expect(lines[0].foldable).toBe(false);
  });

  it('nie formatuje XML powyżej limitu rozmiaru', () => {
    const huge = `<a>${'x'.repeat(XML_FORMAT_LIMIT)}</a>`;

    const lines = toFormattedLines(huge);

    expect(lines).toHaveLength(1);
    expect(lines[0].text).toBe(huge);
  });
});
