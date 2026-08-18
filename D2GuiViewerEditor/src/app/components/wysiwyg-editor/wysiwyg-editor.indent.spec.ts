import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — Tab: wcięcie akapitu bez blockquote', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;
  let editor: HTMLDivElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [WysiwygEditorComponent] }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;

    editor = document.createElement('div');
    editor.className = 'editor-content';
    document.body.appendChild(editor);
    (component as any).getActiveEditor = () => editor;
    (component as any).isSelectionInEditor = () => true;
    (component as any).onContentChange = () => {};
    (component as any).updateFormattingState = () => {};
  });

  afterEach(() => {
    editor.remove();
    window.getSelection()?.removeAllRanges();
  });

  function caretIn(el: Element, offset = 0): void {
    const range = document.createRange();
    range.setStart(el.firstChild ?? el, offset);
    range.collapse(true);
    const sel = window.getSelection()!;
    sel.removeAllRanges();
    sel.addRange(range);
  }

  it('indent ustawia margin-left 48px na akapicie — bez opakowania w blockquote', () => {
    editor.innerHTML = '<p id="p">Tekst akapitu</p>';
    const p = editor.querySelector<HTMLElement>('#p')!;
    caretIn(p);

    component.executeCommand('indent');

    expect(p.style.marginLeft).toBe('48px');
    expect(editor.querySelector('blockquote')).toBeNull();
    expect(p.parentElement).toBe(editor);
  });

  it('kolejne indenty sumują krok na TYM SAMYM elemencie (bez zagnieżdżania)', () => {
    editor.innerHTML = '<p id="p">Tekst</p>';
    const p = editor.querySelector<HTMLElement>('#p')!;

    for (let i = 0; i < 3; i++) {
      caretIn(p);
      component.executeCommand('indent');
    }

    expect(p.style.marginLeft).toBe('144px');
    expect(editor.querySelectorAll('p').length).toBe(1);
    expect(editor.querySelector('blockquote')).toBeNull();
  });

  it('outdent cofa krok, a przy zeru zdejmuje inline margin-left', () => {
    editor.innerHTML = '<p id="p" style="margin-left:96px">Tekst</p>';
    const p = editor.querySelector<HTMLElement>('#p')!;

    caretIn(p);
    component.executeCommand('outdent');
    expect(p.style.marginLeft).toBe('48px');

    caretIn(p);
    component.executeCommand('outdent');
    expect(p.style.marginLeft).toBe('');

    caretIn(p);
    component.executeCommand('outdent');
    expect(p.style.marginLeft).toBe('');
  });

  it('szanuje istniejące wcięcie z importu DOCX (dolicza krok do inline margin-left)', () => {
    editor.innerHTML = '<p id="p" style="margin-left:30px">Wcięty z Worda</p>';
    const p = editor.querySelector<HTMLElement>('#p')!;
    caretIn(p);

    component.executeCommand('indent');

    expect(p.style.marginLeft).toBe('78px');
  });

  it('zaznaczenie przez kilka akapitów wcina każdy z nich', () => {
    editor.innerHTML = '<p id="a">Alfa</p><p id="b">Beta</p><h2 id="c">Gamma</h2>';
    const a = editor.querySelector('#a')!;
    const c = editor.querySelector('#c')!;
    const range = document.createRange();
    range.setStart(a.firstChild!, 1);
    range.setEnd(c.firstChild!, 1);
    const sel = window.getSelection()!;
    sel.removeAllRanges();
    sel.addRange(range);

    component.executeCommand('indent');

    expect((editor.querySelector('#a') as HTMLElement).style.marginLeft).toBe('48px');
    expect((editor.querySelector('#b') as HTMLElement).style.marginLeft).toBe('48px');
    expect((editor.querySelector('#c') as HTMLElement).style.marginLeft).toBe('48px');
  });

  it('akapit wewnątrz blockquote (dokument legacy): wcina się tylko najgłębszy blok', () => {
    editor.innerHTML = '<blockquote id="q"><p id="p">Cytat</p></blockquote>';
    const q = editor.querySelector<HTMLElement>('#q')!;
    const p = editor.querySelector<HTMLElement>('#p')!;
    const range = document.createRange();
    range.selectNodeContents(editor);
    const sel = window.getSelection()!;
    sel.removeAllRanges();
    sel.addRange(range);

    component.executeCommand('indent');

    expect(p.style.marginLeft).toBe('48px');
    expect(q.style.marginLeft).toBe('');
  });
});
