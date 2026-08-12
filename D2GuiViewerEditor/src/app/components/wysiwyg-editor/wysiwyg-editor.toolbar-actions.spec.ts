import { TestBed, ComponentFixture } from '@angular/core/testing';
import { vi } from 'vitest';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — toolbar action guards', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [WysiwygEditorComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;
    (component as any).getActiveEditor = () => document.createElement('div');
  });


  it('rejects font-size 0 / NaN / out-of-range (text must not get 0pt)', () => {
    (component as any).currentFontSize = 11;

    component.setFontSize(0);
    expect((component as any).currentFontSize).toBe(11);

    component.setFontSize(NaN);
    expect((component as any).currentFontSize).toBe(11);

    component.setFontSize(500);
    expect((component as any).currentFontSize).toBe(11);

    component.setFontSize(-3);
    expect((component as any).currentFontSize).toBe(11);
  });

  it('accepts a valid font-size', () => {
    (component as any).currentFontSize = 11;
    component.setFontSize(14);
    expect((component as any).currentFontSize).toBe(14);
  });


  it('normalises link URLs', () => {
    const n = (u: string) => (component as any).normalizeLinkUrl(u);
    expect(n('')).toBeNull();
    expect(n('   ')).toBeNull();
    expect(n('https://x.pl')).toBe('https://x.pl');
    expect(n('http://x.pl')).toBe('http://x.pl');
    expect(n('mailto:a@b.pl')).toBe('mailto:a@b.pl');
    expect(n('#sekcja')).toBe('#sekcja');
    expect(n('/lokalna/sciezka')).toBe('/lokalna/sciezka');
    expect(n('www.example.com')).toBe('https://www.example.com');
    expect(n('example.com/path?x=1')).toBe('https://example.com/path?x=1');
  });

  it('escapes link label HTML', () => {
    expect((component as any).escapeHtml('a<b>&"')).toBe('a&lt;b&gt;&amp;&quot;');
  });

  it('insertLink with empty URL is a no-op (does not throw)', () => {
    expect(() => component.insertLink('')).not.toThrow();
  });


  it('setFontFamily restores the saved selection when focus left the editor (font <select> click)', () => {
    const editor = document.createElement('div');
    (component as any).getActiveEditor = () => editor;
    (component as any).onContentChange = () => {}; // isolate from repaginate/emit
    (component as any).savedSelection = document.createRange();
    const spy = vi.spyOn(component as any, 'restoreSelection').mockImplementation(() => true);

    window.getSelection()?.removeAllRanges();
    component.setFontFamily('Arial');

    expect(spy).toHaveBeenCalled();
    expect((component as any).currentFontFamily).toBe('Arial');
  });


  it('insertText restores the saved selection when focus left the editor (menu paste)', () => {
    const editor = document.createElement('div');
    (component as any).getActiveEditor = () => editor;
    (component as any).onContentChange = () => {}; // isolate from repaginate/emit
    (document as any).execCommand = vi.fn(); // jsdom has no execCommand
    (component as any).savedSelection = document.createRange();
    const spy = vi.spyOn(component as any, 'restoreSelection').mockImplementation(() => true);

    window.getSelection()?.removeAllRanges();
    component.insertText('plain text');

    expect(spy).toHaveBeenCalled();
  });


  it('insertDateField wstawia atomowe pole W AKAPICIE kursora (Range, nie insertHTML)', () => {
    const editor = document.createElement('div');
    const p = document.createElement('p');
    p.textContent = 'Katowice, ';
    editor.appendChild(p);
    document.body.appendChild(editor);
    try {
      (component as any).getActiveEditor = () => editor;
      (component as any).isSelectionInEditor = () => true;
      (component as any).onContentChange = () => {}; // izolacja od repaginacji/emisji

      const range = document.createRange();
      range.selectNodeContents(p);
      range.collapse(false); // karetka na KOŃCU akapitu — dokładnie zgłoszony przypadek
      const sel = window.getSelection()!;
      sel.removeAllRanges();
      sel.addRange(range);

      component.insertDateField();

      const span = p.querySelector('span.field-date') as HTMLElement;
      expect(span, 'pole musi wylądować WEWNĄTRZ akapitu kursora').not.toBeNull();

      const now = new Date();
      const pad = (v: number) => String(v).padStart(2, '0');
      const today = `${pad(now.getDate())}-${pad(now.getMonth() + 1)}-${now.getFullYear()}`;
      expect(span.textContent).toBe(today);
      expect(span.getAttribute('data-fld-instr')).toBe('TIME \\@ "dd-MM-yyyy"');
      expect(span.getAttribute('contenteditable')).toBe('false');
    } finally {
      editor.remove();
    }
  });
});
