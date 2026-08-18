import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';

const ZWS = String.fromCharCode(0x200b);

describe('WysiwygEditorComponent — sticky font po skasowaniu wpisanego tekstu (Problem 10)', () => {
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

  const pending = () => (component as any)._pendingInlineStyle as
    { fontFamily?: string; fontSize?: string } | null;

  it('pełny scenariusz: wybór → pisanie → Backspace → stan raportuje wybrany font → pisanie odtwarza span', () => {
    const editor = mountEditor('<p><br></p>');
    const p = editor.querySelector('p')!;
    caretIn(p, 0);

    component.setFontFamily('Arial');
    const span = editor.querySelector('span')!;
    expect(span.style.fontFamily).toContain('Arial');
    expect(pending()?.fontFamily).toBe('Arial');

    component.onEditorBeforeInput({ inputType: 'insertText' } as InputEvent);
    expect(pending()).toBeNull();
    const textNode = span.firstChild as Text;
    textNode.data = ZWS + 'x';
    caretIn(textNode, 2);

    component.onEditorBeforeInput({ inputType: 'deleteContentBackward' } as InputEvent);
    expect((component as any)._pendingDeleteCapture?.fontFamily).toBe('Arial');
    span.remove();
    caretIn(p, 0);
    (component as any)._consumePendingDeleteCapture();
    expect(pending()?.fontFamily).toBe('Arial');

    (component as any).updateFormattingState();
    expect(component.editorState().currentStyle.fontFamily).toBe('Arial');

    component.onEditorBeforeInput({ inputType: 'insertText' } as InputEvent);
    const newSpan = editor.querySelector('span');
    expect(newSpan).not.toBeNull();
    expect(newSpan!.style.fontFamily).toContain('Arial');
    const sel = window.getSelection()!;
    expect(newSpan!.contains(sel.getRangeAt(0).startContainer)).toBe(true);
    expect(pending()).toBeNull();
  });

  it('setFontSize (zwinięta karetka) uzbraja pending i read-path raportuje wybrany rozmiar', () => {
    const editor = mountEditor('<p><br></p>');
    caretIn(editor.querySelector('p')!, 0);

    component.setFontSize(14);

    expect(pending()?.fontSize).toBe('14pt');
    (component as any).updateFormattingState();
    expect(component.editorState().currentStyle.fontSize).toBe(14);
  });

  it('Backspace w spanie z większą ilością tekstu NIE uzbraja przechwycenia (tani warunek)', () => {
    const editor = mountEditor('<p><span style="font-family: Arial">abc</span></p>');
    const text = editor.querySelector('span')!.firstChild as Text;
    caretIn(text, 3);

    component.onEditorBeforeInput({ inputType: 'deleteContentBackward' } as InputEvent);

    expect((component as any)._pendingDeleteCapture).toBeNull();
  });

  it('przeniesienie karetki gdzie indziej czyści pending (selectionchange) — stan wraca do DOM', () => {
    const editor = mountEditor('<p>abc</p><p>def</p>');
    const paragraphs = Array.from(editor.querySelectorAll('p'));
    caretIn(paragraphs[0].firstChild!, 0);
    (component as any)._pendingInlineStyle = { fontFamily: 'Arial' };
    (component as any)._pendingStyleAnchor = { node: paragraphs[0].firstChild!, offset: 0 };

    caretIn(paragraphs[1].firstChild!, 1);
    (component as any).onSelectionChange();

    expect(pending()).toBeNull();
    (component as any).updateFormattingState();
    expect(component.editorState().currentStyle.fontFamily).not.toBe('Arial');
  });

  it('selectionchange na karetce RÓWNEJ kotwicy nie czyści pending', () => {
    const editor = mountEditor('<p>abc</p>');
    const text = editor.querySelector('p')!.firstChild!;
    caretIn(text, 1);
    (component as any)._pendingInlineStyle = { fontFamily: 'Arial' };
    (component as any)._pendingStyleAnchor = { node: text, offset: 1 };

    (component as any).onSelectionChange();

    expect(pending()?.fontFamily).toBe('Arial');
  });

  it('undo czyści pending (przywrócony DOM unieważnia kotwicę)', () => {
    mountEditor('<p>abc</p>');
    (component as any)._pendingInlineStyle = { fontFamily: 'Arial' };

    component.undo();

    expect(pending()).toBeNull();
  });
});
