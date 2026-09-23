import { diffWords, leftSide, rightSide } from './text-diff.util';

describe('diffWords', () => {
  it('zaznacza tylko zmienione słowa, zachowując wspólne', () => {
    const segments = diffWords('Umowa najmu lokalu', 'Umowa sprzedaży lokalu');

    expect(segments.map((segment) => [segment.kind, segment.text])).toEqual([
      ['same', 'Umowa '],
      ['removed', 'najmu'],
      ['added', 'sprzedaży'],
      ['same', ' lokalu'],
    ]);
  });

  it('rozdziela segmenty na stronę lewą i prawą', () => {
    const segments = diffWords('a b', 'a c');

    expect(leftSide(segments).map((segment) => segment.text).join('')).toBe('a b');
    expect(rightSide(segments).map((segment) => segment.text).join('')).toBe('a c');
  });

  it('identyczne teksty dają jeden segment same, puste — brak segmentów', () => {
    expect(diffWords('x', 'x')).toEqual([{ text: 'x', kind: 'same' }]);
    expect(diffWords('', '')).toEqual([]);
  });

  it('dla bardzo długich tekstów rezygnuje z podświetlania słów', () => {
    const left = Array.from({ length: 600 }, (_, index) => `l${index}`).join(' ');
    const right = Array.from({ length: 600 }, (_, index) => `r${index}`).join(' ');

    const segments = diffWords(left, right);

    expect(segments).toHaveLength(2);
    expect(segments[0].kind).toBe('removed');
    expect(segments[1].kind).toBe('added');
  });
});
