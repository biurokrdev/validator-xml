import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';
import { EditorToolbarComponent } from '../editor-toolbar/editor-toolbar';
import { EditorState } from '../../models/document.model';

describe('WysiwygEditorComponent — stan wyrównania i list dla toolbara', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;
  const mounted: HTMLElement[] = [];

  beforeEach(async () => {
    if (typeof (document as any).queryCommandState !== 'function') {
      (document as any).queryCommandState = () => false;
    }
    await TestBed.configureTestingModule({
      imports: [WysiwygEditorComponent, EditorToolbarComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    mounted.splice(0).forEach(el => el.remove());
    window.getSelection()?.removeAllRanges();
  });

  function mount(html: string): HTMLElement {
    const host = document.createElement('div');
    host.innerHTML = html;
    document.body.appendChild(host);
    mounted.push(host);
    return host;
  }

  function caretIn(node: Node, offset = 0): void {
    const sel = window.getSelection()!;
    const range = document.createRange();
    range.setStart(node, offset);
    range.collapse(true);
    sel.removeAllRanges();
    sel.addRange(range);
  }

  function formatting() {
    (component as any).updateFormattingState();
    return component.editorState().currentFormatting;
  }

  it('akapit bez text-align → wyrównanie „left" (domyślne, jak w Wordzie)', () => {
    const host = mount('<p>zwykły tekst</p>');
    caretIn(host.querySelector('p')!.firstChild!, 2);

    expect(formatting().alignment).toBe('left');
  });

  it('akapit wyśrodkowany (inline, jak po execCommand justifyCenter) → „center"', () => {
    const host = mount('<p style="text-align:center">wyśrodkowany</p>');
    caretIn(host.querySelector('p')!.firstChild!, 1);

    expect(formatting().alignment).toBe('center');
  });

  it('akapit wyjustowany → „justify", do prawej → „right"', () => {
    const host = mount(
      '<p style="text-align:justify">justowany</p><p style="text-align:right">do prawej</p>');
    const [pj, pr] = Array.from(host.querySelectorAll('p'));

    caretIn(pj.firstChild!, 1);
    expect(formatting().alignment).toBe('justify');

    caretIn(pr.firstChild!, 1);
    expect(formatting().alignment).toBe('right');
  });

  it('karetka w li listy punktowanej → bulletList; numerowanej → numberedList', () => {
    const host = mount('<ul><li>punkt</li></ul><ol><li>numer</li></ol>');

    caretIn(host.querySelector('ul li')!.firstChild!, 1);
    let f = formatting();
    expect(f.bulletList).toBe(true);
    expect(f.numberedList).toBe(false);

    caretIn(host.querySelector('ol li')!.firstChild!, 1);
    f = formatting();
    expect(f.bulletList).toBe(false);
    expect(f.numberedList).toBe(true);
  });

  it('zwykły akapit → żadna lista nie jest aktywna', () => {
    const host = mount('<p>tekst</p>');
    caretIn(host.querySelector('p')!.firstChild!, 0);

    const f = formatting();
    expect(f.bulletList).toBe(false);
    expect(f.numberedList).toBe(false);
  });

  it('karetka w liście pól wyboru → checkboxList (a NIE bulletList); zwykła lista DOCX → bulletList', () => {
    const lvl = String.fromCharCode(0xf0a8);
    const host = mount(
      `<ul data-num-id="1" data-num-fmt="bullet" data-lvl-text="${lvl}" data-bullet-font="Wingdings"><li id="x">pole</li></ul>` +
      '<ul data-num-id="2" data-num-fmt="bullet" data-lvl-text="•"><li id="y">kropka</li></ul>');

    caretIn(host.querySelector('#x')!.firstChild!, 1);
    let f = formatting();
    expect(f.checkboxList).toBe(true);
    expect(f.bulletList).toBe(false);

    caretIn(host.querySelector('#y')!.firstChild!, 1);
    f = formatting();
    expect(f.checkboxList).toBe(false);
    expect(f.bulletList).toBe(true);
  });

  describe('EditorToolbarComponent — podświetlenie przycisków', () => {
    function toolbarWith(state: Partial<EditorState> | null): EditorToolbarComponent {
      const tf = TestBed.createComponent(EditorToolbarComponent);
      tf.componentInstance.editorState = state as EditorState | null;
      return tf.componentInstance;
    }

    function stateWithFormatting(patch: Record<string, unknown>): Partial<EditorState> {
      return {
        currentFormatting: {
          bold: false, italic: false, underline: false,
          strikethrough: false, subscript: false, superscript: false,
          ...patch,
        } as EditorState['currentFormatting'],
        currentStyle: {},
      };
    }

    it('dokładnie jeden przycisk wyrównania aktywny; domyślnie „left"', () => {
      const centered = toolbarWith(stateWithFormatting({ alignment: 'center' }));
      expect(centered.isAlignActive('center')).toBe(true);
      expect(centered.isAlignActive('left')).toBe(false);

      const noAlignment = toolbarWith(stateWithFormatting({}));
      expect(noAlignment.isAlignActive('left')).toBe(true);

      const noState = toolbarWith(null);
      expect(noState.isAlignActive('left')).toBe(true);
      expect(noState.isAlignActive('justify')).toBe(false);
    });

    it('isActive raportuje stan list', () => {
      const inBullets = toolbarWith(stateWithFormatting({ bulletList: true }));
      expect(inBullets.isActive('bulletList')).toBe(true);
      expect(inBullets.isActive('numberedList')).toBe(false);
    });
  });
});
