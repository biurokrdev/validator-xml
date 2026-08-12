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


  describe('Tab w tabeli (13259982)', () => {
    let editor: HTMLDivElement;
    let table: HTMLTableElement;

    function setCaret(cell: HTMLTableCellElement): void {
      const range = document.createRange();
      range.setStart(cell, 0);
      range.collapse(true);
      const sel = window.getSelection()!;
      sel.removeAllRanges();
      sel.addRange(range);
    }

    function caretCell(): HTMLTableCellElement | null {
      const sel = window.getSelection();
      const node = sel?.anchorNode;
      const el = node?.nodeType === Node.TEXT_NODE ? node.parentElement : (node as HTMLElement | null);
      return (el?.closest?.('td') ?? null) as HTMLTableCellElement | null;
    }

    beforeEach(() => {
      editor = document.createElement('div');
      editor.setAttribute('contenteditable', 'true');
      table = document.createElement('table');
      table.innerHTML =
        '<tr><td class="docx-borderless-cell" style="border:none;padding:4px;">A1</td><td>B1</td></tr>' +
        '<tr><td>A2</td><td>B2</td></tr>';
      editor.appendChild(table);
      document.body.appendChild(editor);
      (component as any).getActiveEditor = () => editor;
      (component as any).onContentChange = () => {};
    });

    afterEach(() => editor.remove());

    it('Tab przechodzi do następnej komórki, na końcu wiersza do pierwszej w kolejnym', () => {
      setCaret(table.rows[0].cells[0]);
      expect((component as any)._handleTableTab(false)).toBe(true);
      expect(caretCell()?.textContent).toBe('B1');

      expect((component as any)._handleTableTab(false)).toBe(true);
      expect(caretCell()?.textContent).toBe('A2');
    });

    it('Shift+Tab wraca do poprzedniej komórki i do ostatniej w poprzednim wierszu', () => {
      setCaret(table.rows[1].cells[0]);
      expect((component as any)._handleTableTab(true)).toBe(true);
      expect(caretCell()?.textContent).toBe('B1');

      expect((component as any)._handleTableTab(true)).toBe(true);
      expect(caretCell()?.textContent).toBe('A1');
      expect((component as any)._handleTableTab(true)).toBe(true);
      expect(caretCell()?.textContent).toBe('A1');
    });

    it('Tab w OSTATNIEJ komórce dokłada wiersz dziedziczący atrybuty wzorca', () => {
      setCaret(table.rows[1].cells[1]);
      expect((component as any)._handleTableTab(false)).toBe(true);

      expect(table.rows).toHaveLength(3);
      expect(caretCell()).toBe(table.rows[2].cells[0]);
      expect(table.rows[2].cells).toHaveLength(2);
    });

    it('poza tabelą Tab NIE jest konsumowany (zostaje wcięcie)', () => {
      const p = document.createElement('p');
      p.textContent = 'zwykly akapit';
      editor.appendChild(p);
      const range = document.createRange();
      range.setStart(p.firstChild!, 0);
      range.collapse(true);
      const sel = window.getSelection()!;
      sel.removeAllRanges();
      sel.addRange(range);

      expect((component as any)._handleTableTab(false)).toBe(false);
    });
  });


  it('insertTable ustawia karetkę w PIERWSZEJ komórce nowej tabeli', () => {
    const editor = document.createElement('div');
    editor.setAttribute('contenteditable', 'true');
    document.body.appendChild(editor);
    try {
      (component as any).getActiveEditor = () => editor;
      (component as any).onContentChange = () => {};
      window.getSelection()?.removeAllRanges();

      component.insertTable('2x2');

      const firstCell = editor.querySelector('td')!;
      const sel = window.getSelection()!;
      expect(sel.rangeCount).toBe(1);
      const anchor = sel.anchorNode!;
      const anchorEl = anchor.nodeType === Node.TEXT_NODE ? anchor.parentElement : (anchor as HTMLElement);
      expect(anchorEl === firstCell || firstCell.contains(anchorEl)).toBe(true);
    } finally {
      editor.remove();
    }
  });
});
