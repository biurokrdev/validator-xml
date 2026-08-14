import {
  wordSingleFactor,
  applyWordLineSpacing,
  applyExactLineSpacing,
  readWordLineMultiple,
  DEFAULT_SINGLE_FACTOR,
} from './word-line-spacing.util';

/**
 * Kalibracja interlinii mnożnikowej Word ↔ CSS (PG-09) po stronie GUI — lustro
 * backendowego WordLineSpacing.cs. Mnożnik Worda (N × pojedynczy odstęp z metryk
 * fontu) ≠ bezjednostkowe CSS line-height (N × font-size); GUI ustawia wartość
 * skalibrowaną + marker --w-line-tw i odczytuje mnożnik z markera.
 */
describe('word-line-spacing.util — kalibracja interlinii Worda (PG-09)', () => {
  let el: HTMLElement;

  beforeEach(() => {
    el = document.createElement('p');
    document.body.appendChild(el);
  });

  afterEach(() => el.remove());

  describe('wordSingleFactor', () => {
    it('zna metryki Calibri (1.221) niezależnie od cudzysłowów i fallbacków listy', () => {
      expect(wordSingleFactor("'Calibri', sans-serif")).toBe(1.221);
      expect(wordSingleFactor('Calibri')).toBe(1.221);
      expect(wordSingleFactor('"Times New Roman", serif')).toBe(1.149);
    });

    it('nieznany/firmowy krój i brak fontu → fallback 1.2', () => {
      expect(wordSingleFactor('Qutas Me')).toBe(DEFAULT_SINGLE_FACTOR);
      expect(wordSingleFactor(null)).toBe(DEFAULT_SINGLE_FACTOR);
      expect(wordSingleFactor('')).toBe(DEFAULT_SINGLE_FACTOR);
    });
  });

  describe('applyWordLineSpacing', () => {
    it('mnożnik 1,08 przy Calibri → skalibrowane line-height + marker 259 (dokładny w:line)', () => {
      el.style.fontFamily = 'Calibri';
      applyWordLineSpacing(el, 1.08);

      expect(el.style.lineHeight).toBe('1.319'); // 1.08 × 1.221
      expect(el.style.getPropertyValue('--w-line-tw')).toBe('259'); // 1.08 × 240
    });

    it('mnożnik 1,5 bez znanego fontu → fallback 1.2 (line-height 1.8, marker 360)', () => {
      applyWordLineSpacing(el, 1.5);

      expect(el.style.lineHeight).toBe('1.8');
      expect(el.style.getPropertyValue('--w-line-tw')).toBe('360');
    });
  });

  describe('applyExactLineSpacing', () => {
    it('exactly: pt bez markera mnożnika (usuwa przeterminowany)', () => {
      el.style.setProperty('--w-line-tw', '240');
      applyExactLineSpacing(el, 18, false);

      expect(el.style.lineHeight).toBe('18pt');
      expect(el.style.getPropertyValue('--w-line-tw')).toBe('');
      expect(el.style.getPropertyValue('--w-line-rule')).toBe('');
    });

    it('atLeast: max(pt, single) + marker reguły (PG-10 — linia ROŚNIE gdy treść wyższa)', () => {
      applyExactLineSpacing(el, 18, true);

      expect(el.style.lineHeight).toBe('max(18pt, var(--w-line-single, 1.2em))');
      expect(el.style.getPropertyValue('--w-line-rule')).toBe('atLeast');
    });

    it('przełączenie atLeast → exactly zdejmuje marker reguły', () => {
      applyExactLineSpacing(el, 18, true);
      applyExactLineSpacing(el, 18, false);

      expect(el.style.getPropertyValue('--w-line-rule')).toBe('');
    });
  });

  describe('readWordLineMultiple', () => {
    it('preferuje marker --w-line-tw nad skalibrowaną wartością renderową', () => {
      el.style.fontFamily = 'Calibri';
      applyWordLineSpacing(el, 1.08);

      expect(readWordLineMultiple(el)).toBe(1.08); // 259/240 ≈ 1.08, nie 1.319
    });

    it('round-trip apply → read zwraca zadany mnożnik dla typowych wartości', () => {
      for (const multiple of [1, 1.15, 1.5, 2]) {
        applyWordLineSpacing(el, multiple);
        expect(readWordLineMultiple(el)).toBe(multiple);
      }
    });

    it('starsza treść bez markera: bezjednostkowa wartość = mnożnik Worda', () => {
      el.style.lineHeight = '1.5';

      expect(readWordLineMultiple(el)).toBe(1.5);
    });

    it('inline pt (exact/atLeast) → null (to nie mnożnik)', () => {
      el.style.lineHeight = '18pt';

      expect(readWordLineMultiple(el)).toBeNull();
    });
  });
});
