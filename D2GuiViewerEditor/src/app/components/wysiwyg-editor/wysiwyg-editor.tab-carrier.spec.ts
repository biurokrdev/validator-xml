import { TestBed, ComponentFixture } from '@angular/core/testing';
import { vi } from 'vitest';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — nośnik tabulatora i TAB w tabeli', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;
  const mounted: HTMLElement[] = [];

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [WysiwygEditorComponent] }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    mounted.splice(0).forEach(el => el.remove());
  });

  function editorWith(html: string): HTMLElement {
    const editor = document.createElement('div');
    editor.className = 'editor-content';
    editor.setAttribute('contenteditable', 'true');
    editor.innerHTML = html;
    document.body.appendChild(editor);
    mounted.push(editor);
    (component as any).pageEditorRefs = { toArray: () => [{ nativeElement: editor }] };
    (component as any).editorContent = { nativeElement: editor };
    (component as any).getActiveEditor = () => editor;
    return editor;
  }

  it('TAB w ostatniej komórce: nowy wiersz + natychmiastowa repaginacja (_flushPaginateSoon)', () => {
    const editor = editorWith('<table><tbody><tr><td>a</td><td>b</td></tr></tbody></table>');
    const flush = vi.fn();
    (component as any)._flushPaginateSoon = flush;
    (component as any).onContentChange = vi.fn();
    const lastCell = editor.querySelectorAll('td')[1];
    const sel = window.getSelection()!;
    const r = document.createRange(); r.selectNodeContents(lastCell); r.collapse(false);
    sel.removeAllRanges(); sel.addRange(r);

    const handled = (component as any)._handleTableTab(false);

    expect(handled).toBe(true);
    expect(editor.querySelectorAll('tr').length).toBe(2);
    expect(flush).toHaveBeenCalledTimes(1);
  });

  it('nośnik wstawiony przez TAB ma white-space:pre i tab-size:2em', () => {
    const editor = editorWith('<p>ab</p>');
    (component as any).onContentChange = vi.fn();
    (component as any)._schedulePaginate = vi.fn();
    const text = editor.querySelector('p')!.firstChild as Text;
    const sel = window.getSelection()!;
    const r = document.createRange(); r.setStart(text, 1); r.collapse(true);
    sel.removeAllRanges(); sel.addRange(r);

    (component as any)._insertTabAtCaret();

    const span = editor.querySelector('span[contenteditable="false"]') as HTMLElement;
    expect(span.getAttribute('style')).toBe('display:inline-block;min-width:2em;white-space:pre;tab-size:2em');
    expect(span.textContent).toBe('\t');
    expect((component as any)._schedulePaginate).toHaveBeenCalled();
  });

  it('karetka wewnątrz nośnika po strzałce w prawo ląduje ZA nośnikiem, w lewo — PRZED', () => {
    const editor = editorWith('<p><span style="font-size:10pt">DO</span><span style="font-size:10pt"><span style="display:inline-block;min-width:2em;white-space:pre;tab-size:2em" contenteditable="false">\t</span></span><span style="font-size:10pt">UPPER</span></p>');
    const carrier = editor.querySelector('span[contenteditable="false"]')!;
    const sel = window.getSelection()!;
    const inside = document.createRange(); inside.setStart(carrier.firstChild!, 1); inside.collapse(true);
    sel.removeAllRanges(); sel.addRange(inside);

    const wrapper = carrier.parentElement!;
    const p = wrapper.parentElement!;
    (component as any)._escapeTabCarrier('right');
    let rng = sel.getRangeAt(0);
    expect(rng.startContainer).toBe(wrapper.nextSibling!.firstChild);
    expect(rng.startOffset).toBe(0);

    sel.removeAllRanges(); inside.setStart(carrier.firstChild!, 0); inside.collapse(true); sel.addRange(inside);
    (component as any)._escapeTabCarrier('left');
    rng = sel.getRangeAt(0);
    expect(rng.startContainer).toBe(wrapper.previousSibling!.firstChild);
    expect(rng.startOffset).toBe(2);
    void p;
  });

  it('pozycja (wrapper, offset za nośnikiem) — bez prostokąta karetki — też jest wypychana za wrapper', () => {
    const editor = editorWith('<p><span>DO</span><span><span style="display:inline-block;min-width:2em;white-space:pre;tab-size:2em" contenteditable="false">	</span></span><span>UPPER</span></p>');
    const wrapper = editor.querySelector('span[contenteditable="false"]')!.parentElement!;
    const p = wrapper.parentElement!;
    const sel = window.getSelection()!;
    const r = document.createRange(); r.setStart(wrapper, 1); r.collapse(true);
    sel.removeAllRanges(); sel.addRange(r);

    (component as any)._escapeTabCarrier('right');

    const rng = sel.getRangeAt(0);
    expect(rng.startContainer).toBe(p.lastChild!.firstChild);
    expect(rng.startOffset).toBe(0);
  });

  it('nośnik na końcu akapitu (brak tekstu za nim) — karetka ląduje ZA nośnikiem', () => {
    const editor = editorWith('<p>abc<span style="display:inline-block;min-width:2em;white-space:pre;tab-size:2em" contenteditable="false">	</span></p>');
    const carrier = editor.querySelector('span[contenteditable="false"]')!;
    const p = carrier.parentElement!;
    const sel = window.getSelection()!;
    const r = document.createRange(); r.setStart(carrier.firstChild!, 1); r.collapse(true);
    sel.removeAllRanges(); sel.addRange(r);

    (component as any)._escapeTabCarrier('right');

    const rng = sel.getRangeAt(0);
    expect(rng.startContainer).toBe(p);
    expect(rng.startOffset).toBe(2);
  });

  it('karetka poza nośnikiem nie jest ruszana', () => {
    const editor = editorWith('<p>abc<span style="display:inline-block;min-width:2em;white-space:pre;tab-size:2em" contenteditable="false">\t</span>def</p>');
    const text = editor.querySelector('p')!.firstChild as Text;
    const sel = window.getSelection()!;
    const r = document.createRange(); r.setStart(text, 2); r.collapse(true);
    sel.removeAllRanges(); sel.addRange(r);

    (component as any)._escapeTabCarrier('right');

    const rng = sel.getRangeAt(0);
    expect(rng.startContainer).toBe(text);
    expect(rng.startOffset).toBe(2);
  });
});
