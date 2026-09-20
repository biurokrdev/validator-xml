import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — poziomy list (Tab / Shift+Tab / Enter)', () => {
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
    (component as any).refreshListLabels = () => {};
    (component as any)._flushPaginateSoon = () => {};
  });

  afterEach(() => editor.remove());

  const L0 = 'data-num-id="4" data-abstract-num-id="2" data-ilvl="0" data-num-fmt="decimal" data-lvl-text="%1."' +
    ' data-ind-left-tw="360" data-ind-hanging-tw="360" style="margin:0;padding-left:24px;list-style-type:decimal;--ind-hanging:24px;"';
  const L1 = 'data-num-id="4" data-abstract-num-id="2" data-ilvl="1" data-num-fmt="decimal" data-lvl-text="%1.%2."' +
    ' data-ind-left-tw="792" data-ind-hanging-tw="432" style="margin:0;padding-left:28.8px;list-style-type:decimal;--ind-hanging:28.8px;"';

  function importedOutline(): void {
    editor.innerHTML =
      `<ol ${L0}><li id="a">A<ol ${L1}><li id="a1">A.1</li><li id="a2">A.2</li></ol></li>` +
      `<li id="b">B<ol ${L1}><li id="b1">B.1</li></ol></li></ol>`;
  }

  function caretAtStart(id: string): void {
    const li = editor.querySelector(`#${id}`)!;
    const text = Array.from(li.childNodes).find(n => n.nodeType === Node.TEXT_NODE)!;
    const range = document.createRange();
    range.setStart(text, 0);
    range.collapse(true);
    const sel = window.getSelection()!;
    sel.removeAllRanges();
    sel.addRange(range);
  }

  const tab = (shift = false): boolean => (component as any)._handleChecklistTab(shift);
  const topLevel = (): string[] =>
    Array.from(editor.children).map(e => `${e.tagName}@${e.getAttribute('data-ilvl')}:${Array.from(e.children).map(c => c.id).join(',')}`);

  it('Tab: punkt przechodzi na poziom 2 TEJ SAMEJ listy; jego dzieci zostają na swoim poziomie (sklejone z nim)', () => {
    importedOutline();
    caretAtStart('b');

    expect(tab()).toBe(true);

    expect(topLevel()).toEqual(['OL@0:a', 'OL@1:b,b1']);
    const moved = editor.querySelector('#b')!.parentElement as HTMLElement;
    expect(moved.getAttribute('data-num-id')).toBe('4');
    expect(moved.getAttribute('data-lvl-text')).toBe('%1.%2.');
    expect(moved.getAttribute('data-ind-left-tw')).toBe('792');
    expect(parseFloat(moved.style.paddingLeft)).toBeCloseTo(52.8, 1);
    expect(editor.querySelector('#b ol')).toBeNull();
    expect(editor.querySelector('ol ol ol')).toBeNull();
  });

  it('Shift+Tab na punkcie zagnieżdżonym: wychodzi za gospodarza, kolejne punkty zostają jego dziećmi', () => {
    importedOutline();
    caretAtStart('a1');

    expect(tab(true)).toBe(true);

    const top = editor.querySelector(':scope > ol')!;
    expect(Array.from(top.children).map(li => li.id)).toEqual(['a', 'a1', 'b']);
    expect(editor.querySelector('#a ol')).toBeNull();
    const child = editor.querySelector('#a1 > ol') as HTMLElement;
    expect(Array.from(child.children).map(li => li.id)).toEqual(['a2']);
    expect(child.getAttribute('data-ilvl')).toBe('1');
    expect(parseFloat(child.style.paddingLeft)).toBeCloseTo(28.8, 1);
  });

  it('poziom NIEużyty w treści bierze definicję z importu (data-list-abstracts), nie drabinkę domyślną', () => {
    editor.innerHTML =
      '<ol data-num-id="7" data-abstract-num-id="5" data-ilvl="0" data-num-fmt="decimal" data-lvl-text="%1)"' +
      ' style="margin:0;padding-left:24px;list-style-type:decimal;--ind-hanging:24px;"><li id="x">X</li><li id="y">Y</li></ol>';
    (component as any)._documentContainerAttrs = [{
      name: 'data-list-abstracts',
      value: JSON.stringify({
        '5': { mlt: 'multilevel', levels: [
          { ilvl: '0', tag: 'ol', 'list-style': 'decimal', 'num-fmt': 'decimal', 'lvl-text': '%1)', 'ind-left-tw': '360', 'ind-hanging-tw': '360' },
          { ilvl: '1', tag: 'ol', 'list-style': 'lower-alpha', 'num-fmt': 'lowerLetter', 'lvl-text': '%2)', 'ind-left-tw': '720', 'ind-hanging-tw': '360' },
        ] },
      }),
    }];
    caretAtStart('y');

    tab();

    const moved = editor.querySelector('#y')!.parentElement as HTMLElement;
    expect(moved.getAttribute('data-ilvl')).toBe('1');
    expect(moved.getAttribute('data-num-fmt')).toBe('lowerLetter');
    expect(moved.getAttribute('data-lvl-text')).toBe('%2)');
    expect(moved.getAttribute('data-num-id')).toBe('7');
    expect(moved.style.paddingLeft).toBe('48px');
    expect(moved.style.getPropertyValue('--ind-hanging')).toBe('24px');
  });

  it('zmiana poziomu na PUNKTOR: kontener zmienia znacznik ol→ul, natywny punktor nie dostaje dodatkowego spana', () => {
    editor.innerHTML =
      '<ol data-num-id="7" data-abstract-num-id="5" data-ilvl="0" data-num-fmt="decimal" data-lvl-text="%1."' +
      ' style="margin:0;padding-left:24px;list-style-type:decimal;"><li id="x">X</li><li id="y">Y</li></ol>';
    (component as any)._documentContainerAttrs = [{
      name: 'data-list-abstracts',
      value: JSON.stringify({ '5': { levels: [
        { ilvl: '1', tag: 'ul', 'list-style': 'disc', 'num-fmt': 'bullet', 'lvl-text': String.fromCharCode(0xf0b7), 'bullet-font': 'Symbol', 'ind-left-tw': '720', 'ind-hanging-tw': '360' },
      ] } }),
    }];
    caretAtStart('y');

    tab();

    const moved = editor.querySelector('#y')!.parentElement as HTMLElement;
    expect(moved.tagName).toBe('UL');
    expect(moved.style.listStyleType).toBe('disc');
    expect(editor.querySelector('#y .list-marker')).toBeNull();
  });

  it('lista bez definicji poziomu docelowego: ten sam format przesunięty o poziom, „%1." → „%2."', () => {
    editor.innerHTML =
      '<ol data-num-id="7" data-ilvl="0" data-num-fmt="decimal" data-lvl-text="%1." data-ind-left-tw="720"' +
      ' style="padding-left:48px;"><li id="x">X</li><li id="y">Y</li></ol>';
    caretAtStart('y');

    tab();

    const moved = editor.querySelector('#y')!.parentElement as HTMLElement;
    expect(moved.getAttribute('data-ilvl')).toBe('1');
    expect(moved.getAttribute('data-lvl-text')).toBe('%2.');
    expect(moved.getAttribute('data-ind-left-tw')).toBe('1440');
    expect(moved.style.paddingLeft).toBe('96px');
  });

  it('lista utworzona w edytorze (bez kontraktu): Tab zagnieżdża w poprzednim punkcie, pierwszy punkt bez zmian', () => {
    editor.innerHTML = '<ul><li id="x">X</li><li id="y">Y</li><li id="z">Z</li></ul>';

    caretAtStart('x');
    tab();
    expect(editor.querySelectorAll('ul').length).toBe(1);

    caretAtStart('y');
    tab();
    caretAtStart('z');
    tab();

    const nested = editor.querySelector('#x > ul')!;
    expect(Array.from(nested.children).map(li => li.id)).toEqual(['y', 'z']);
    expect(editor.querySelector('ul > ul')).toBeNull();

    caretAtStart('y');
    tab(true);
    expect(Array.from(editor.querySelector(':scope > ul')!.children).map(li => li.id)).toEqual(['x', 'y']);
    expect(editor.querySelector('#y > ul > li')!.id).toBe('z');
  });

  it('Tab w środku tekstu punktu zwykłej listy nie zmienia poziomu (wołający wstawi tabulator)', () => {
    importedOutline();
    const text = editor.querySelector('#b')!.firstChild!;
    const range = document.createRange();
    range.setStart(text, 1);
    range.collapse(true);
    window.getSelection()!.removeAllRanges();
    window.getSelection()!.addRange(range);

    expect(tab()).toBe(false);
    expect(topLevel()).toEqual(['OL@0:a,b']);
  });

  it('fragmenty RÓŻNYCH list o tym samym formacie nie są sklejane (np. lista rozpoczęta od nowa)', () => {
    const other = L1.replace('data-num-id="4"', 'data-num-id="9"');
    editor.innerHTML = `<ol ${other}><li id="p">P</li></ol><ol ${L0}><li id="a">A</li><li id="b">B</li></ol>`;
    caretAtStart('a');

    tab();

    expect(editor.querySelector('#a')!.parentElement).not.toBe(editor.querySelector('#p')!.parentElement);
    expect(editor.querySelector('#a')!.parentElement!.getAttribute('data-num-id')).toBe('4');
  });

  it('Enter na PUSTYM punkcie poziomu 2 cofa go o poziom; na poziomie 1 — wychodzi z listy', () => {
    editor.innerHTML = `<ol ${L0}><li id="a">A</li></ol><ol ${L1}><li id="e"><br></li></ol>`;
    const li = editor.querySelector('#e')!;
    const range = document.createRange();
    range.setStart(li, 0);
    range.collapse(true);
    window.getSelection()!.removeAllRanges();
    window.getSelection()!.addRange(range);

    expect((component as any)._handleChecklistEnter()).toBe(true);
    expect(editor.querySelector('#e')!.parentElement!.getAttribute('data-ilvl')).toBe('0');
    expect(editor.querySelectorAll('ol').length).toBe(1);

    const r2 = document.createRange();
    r2.setStart(editor.querySelector('#e')!, 0);
    r2.collapse(true);
    window.getSelection()!.removeAllRanges();
    window.getSelection()!.addRange(r2);
    expect((component as any)._handleChecklistEnter()).toBe(true);
    expect(editor.querySelector('#e')).toBeNull();
    expect(editor.lastElementChild!.tagName).toBe('P');
  });

  it('Enter w NIEpustym punkcie zwykłej listy zostaje przy przeglądarce', () => {
    importedOutline();
    caretAtStart('a1');
    expect((component as any)._handleChecklistEnter()).toBe(false);
  });
});
