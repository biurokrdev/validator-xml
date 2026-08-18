import { describe, it, expect } from 'vitest';
import { htmlToText, normalizeWhitespace, resolvePlainText } from './paste-text.util';

describe('normalizeWhitespace', () => {
  it('normalizes CRLF and CR to LF', () => {
    expect(normalizeWhitespace('a\r\nb\rc')).toBe('a\nb\nc');
  });

  it('preserves runs of spaces (Word "Keep Text Only" behavior), trims line ends', () => {
    expect(normalizeWhitespace('a    b   \nc')).toBe('a    b\nc');
  });

  it('caps consecutive blank lines at one', () => {
    expect(normalizeWhitespace('a\n\n\n\nb')).toBe('a\n\nb');
  });

  it('converts non-breaking space to a normal space', () => {
    expect(normalizeWhitespace('a b')).toBe('a b');
  });

  it('strips zero-width characters', () => {
    expect(normalizeWhitespace('a​b﻿')).toBe('ab');
  });

  it('preserves tabs (Excel/TSV column structure)', () => {
    expect(normalizeWhitespace('a\tb\tc')).toBe('a\tb\tc');
  });
});

describe('htmlToText', () => {
  it('strips inline formatting, keeps the text', () => {
    expect(htmlToText('<p><b>Ala</b> ma <i>kota</i></p>')).toBe('Ala ma kota');
  });

  it('keeps paragraph breaks', () => {
    expect(htmlToText('<p>one</p><p>two</p>')).toBe('one\ntwo');
  });

  it('turns <br> into a line break', () => {
    expect(htmlToText('a<br>b')).toBe('a\nb');
  });

  it('renders unordered lists with bullet markers', () => {
    expect(htmlToText('<ul><li>x</li><li>y</li></ul>')).toBe('• x\n• y');
  });

  it('renders ordered lists with numeric markers', () => {
    expect(htmlToText('<ol><li>x</li><li>y</li></ol>')).toBe('1. x\n2. y');
  });

  it('tab-separates table cells and newline-separates rows', () => {
    const html = '<table><tr><td>a</td><td>b</td></tr><tr><td>c</td><td>d</td></tr></table>';
    expect(htmlToText(html)).toBe('a\tb\nc\td');
  });

  it('keeps anchor text and drops the URL', () => {
    expect(htmlToText('See <a href="https://x.test">link</a>')).toBe('See link');
  });

  it('does not emit script/style content', () => {
    expect(htmlToText('<p>x</p><script>alert(1)</script><style>p{}</style>')).toBe('x');
  });
});

describe('resolvePlainText', () => {
  it('prefers source-provided text/plain', () => {
    expect(resolvePlainText('plain', '<b>html</b>')).toBe('plain');
  });

  it('falls back to converting html when plain is empty', () => {
    expect(resolvePlainText('', '<p><b>x</b></p>')).toBe('x');
  });

  it('returns empty string when nothing is available', () => {
    expect(resolvePlainText('', '')).toBe('');
  });

  it('multiple consecutive spaces survive resolvePlainText', () => {
    expect(resolvePlainText('kol 1:    wartość', '')).toBe('kol 1:    wartość');
  });
});
