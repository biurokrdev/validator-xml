import { TestBed, ComponentFixture } from '@angular/core/testing';
import { vi } from 'vitest';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — font-family at caret and selection', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;
  const mounted: HTMLElement[] = [];

  beforeEach(async () => {
    
    if (typeof (document as any).queryCommandState !== 'function') {
      (document as any).queryCommandState = () => false;
    }
    await TestBed.configureTestingModule({
      imports: [WysiwygEditorComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    mounted.splice(0).forEach(el => el.remove());
    window.getSelection()?.removeAllRanges();
  });

  function mountEditor(html: string): HTMLDivElement {
    const editor = document.createElement('div');
    editor.setAttribute('contenteditable', 'true');
    editor.innerHTML = html;
    document.body.appendChild(editor);
    mounted.push(editor);
    
    (component as any).editorContent = { nativeElement: editor };
    (component as any).onContentChange = () => {};
    return editor;
  }

  function caretIn(node: Node, offset: number): void {
    const sel = window.getSelection()!;
    const range = document.createRange();
    range.setStart(node, offset);
    range.collapse(true);
    sel.removeAllRanges();
    sel.addRange(range);
  }

  function fontFamilyOf(el: Element | null): string {
    return (el as HTMLElement | null)?.style.fontFamily ?? '';
  }

  it('caret without selection: wraps a ZWS span in the chosen font and anchors the caret inside it', () => {
    const editor = mountEditor('<p><br></p>');
    caretIn(editor.querySelector('p')!, 0);

    component.setFontFamily('Arial');

    const span = editor.querySelector('span');
    expect(span).not.toBeNull();
    expect(fontFamilyOf(span)).toContain('Arial');
    expect(span!.textContent).toBe('​');

    const sel = window.getSelection()!;
    expect(sel.rangeCount).toBe(1);
    expect(span!.contains(sel.getRangeAt(0).startContainer)).toBe(true);
  });

  it('caret path keeps savedSelection + toolbar state in sync (parity with setFontSize)', () => {
    const editor = mountEditor('<p><br></p>');
    caretIn(editor.querySelector('p')!, 0);
    const stateSpy = vi.spyOn(component as any, 'updateFormattingState');

    component.setFontFamily('Verdana');

    const span = editor.querySelector('span')!;
    
    const saved = (component as any).savedSelection as Range | null;
    expect(saved).not.toBeNull();
    expect(span.contains(saved!.startContainer)).toBe(true);
    expect(stateSpy).toHaveBeenCalled();
  });

  it('second pick at the same caret reuses the ZWS span instead of nesting a new one', () => {
    const editor = mountEditor('<p><br></p>');
    caretIn(editor.querySelector('p')!, 0);

    component.setFontFamily('Arial');
    component.setFontFamily('Times New Roman');

    const spans = editor.querySelectorAll('span');
    expect(spans.length).toBe(1);
    expect(fontFamilyOf(spans[0])).toContain('Times New Roman');
    expect(spans[0].textContent).toBe('​');
  });

  it('selection: wraps the selected text in the chosen font (existing text kept)', () => {
    const editor = mountEditor('<p>alfa beta</p>');
    const text = editor.querySelector('p')!.firstChild!;
    const sel = window.getSelection()!;
    const range = document.createRange();
    range.setStart(text, 0);
    range.setEnd(text, 4); 
    sel.removeAllRanges();
    sel.addRange(range);

    (component as any).applyFontFamilyToSelection('Georgia', sel, range);

    const span = Array.from(editor.querySelectorAll('span'))
      .find(s => (s.style.fontFamily || '').includes('Georgia'));
    expect(span).toBeTruthy();
    expect(span!.textContent).toBe('alfa');
    expect(editor.textContent).toBe('alfa beta');
  });

  it('does nothing without an active editor (guarded)', () => {
    (component as any).editorContent = null;
    expect(() => component.setFontFamily('Arial')).not.toThrow();
  });
});
