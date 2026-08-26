import { describe, it, expect, beforeEach } from 'vitest';
import {
  PARAGRAPH_SPACING_SUM_CLASS,
  isSumSpacingModel,
  readParagraphSpaceAfterPx,
  readParagraphSpaceBeforePx,
  setParagraphSpaceAfter,
  setParagraphSpaceBefore,
} from './paragraph-spacing.util';

describe('paragraph-spacing.util (ADR-0107)', () => {
  let page: HTMLElement;
  let p: HTMLElement;

  beforeEach(() => {
    document.body.innerHTML = '';
    page = document.createElement('div');
    page.className = 'editor-content';
    p = document.createElement('p');
    page.appendChild(p);
    document.body.appendChild(page);
  });

  it('domyślnie (model max) „po" = margin-bottom, padding-bottom czyszczony', () => {
    p.style.paddingBottom = '10pt';
    setParagraphSpaceAfter(p, '12pt');
    expect(p.style.marginBottom).toBe('12pt');
    expect(p.style.paddingBottom).toBe('');
    expect(isSumSpacingModel(p)).toBe(false);
  });

  it('model sumy (klasa strony) → „po" = padding-bottom, margin-bottom czyszczony', () => {
    page.classList.add(PARAGRAPH_SPACING_SUM_CLASS);
    p.style.marginBottom = '10pt';
    setParagraphSpaceAfter(p, '12pt');
    expect(p.style.paddingBottom).toBe('12pt');
    expect(p.style.marginBottom).toBe('');
    expect(isSumSpacingModel(p)).toBe(true);
  });

  it('edycja w pt zdejmuje markery --w-before-lines / --w-after-lines', () => {
    p.style.setProperty('--w-before-lines', '100');
    p.style.setProperty('--w-after-lines', '150');
    setParagraphSpaceBefore(p, '6pt');
    setParagraphSpaceAfter(p, '8pt');
    expect(p.style.getPropertyValue('--w-before-lines')).toBe('');
    expect(p.style.getPropertyValue('--w-after-lines')).toBe('');
    expect(p.style.marginTop).toBe('6pt');
  });

  it('odczyt preferuje wartość zadeklarowaną inline (pt → px) nad kaskadą', () => {
    p.style.marginTop = '12pt';
    p.style.marginBottom = '6pt';
    expect(readParagraphSpaceBeforePx(p)).toBeCloseTo(16, 5);
    expect(readParagraphSpaceAfterPx(p)).toBeCloseTo(8, 5);
  });

  it('odczyt „po" sumuje oba nośniki (jeden jest zawsze 0)', () => {
    p.style.paddingBottom = '8px';
    p.style.marginBottom = '0';
    expect(readParagraphSpaceAfterPx(p)).toBe(8);
  });
});
