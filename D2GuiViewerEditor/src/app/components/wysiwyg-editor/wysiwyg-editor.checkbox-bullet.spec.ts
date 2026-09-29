import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';
import { ensureBulletMarkers, bulletGlyphFromContract } from '../../core/utils/list-label.util';
import { CheckboxMarkService } from '../../services/checkbox-mark.service';

const PUA_UNCHECKED = String.fromCharCode(0xf071);
const PUA_CHECKED = String.fromCharCode(0xf0fd);
const PUA_CHECKED_TICKBOX = String.fromCharCode(0xf0fe);

describe('WysiwygEditorComponent — punktory-checkboxy', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;
  let editor: HTMLDivElement;

  beforeEach(async () => {
    localStorage.removeItem('d2.editor.checkboxMark');
    await TestBed.configureTestingModule({ imports: [WysiwygEditorComponent] }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;

    editor = document.createElement('div');
    editor.className = 'editor-content';
    document.body.appendChild(editor);
    (component as any).getActiveEditor = () => editor;
    (component as any).isSelectionInEditor = () => true;
    (component as any).onContentChange = () => {};
  });

  afterEach(() => editor.remove());

  function checkboxList(): void {
    editor.innerHTML =
      `<ul data-num-id="5" data-abstract-num-id="3" data-ilvl="0" data-num-fmt="bullet"` +
      ` data-lvl-text="${PUA_UNCHECKED}" data-bullet-font="Wingdings">` +
      '<li id="a"><span class="list-marker" contenteditable="false">❑</span>Alfa</li>' +
      '<li id="b"><span class="list-marker" contenteditable="false">❑</span>Beta</li>' +
      '<li id="c"><span class="list-marker" contenteditable="false">❑</span>Gamma</li></ul>';
  }

  function caretInLi(id: string): void {
    const li = editor.querySelector(`#${id}`)!;
    const textNode = li.lastChild!;
    const range = document.createRange();
    range.setStart(textNode, 1);
    range.collapse(true);
    const sel = window.getSelection()!;
    sel.removeAllRanges();
    sel.addRange(range);
  }

  it('przełącza punktor środkowego li na odhaczony — tylko ten punkt, w kodowaniu PUA', () => {
    checkboxList();
    caretInLi('b');

    component.toggleCheckboxBullet();

    const lists = Array.from(editor.querySelectorAll('ul'));
    expect(lists.length).toBe(3);

    const solo = editor.querySelector('#b')!.parentElement!;
    expect(solo.getAttribute('data-lvl-text')).toBe(PUA_CHECKED);
    expect(solo.getAttribute('data-lvl-override')).toBe('1');
    expect(solo.getAttribute('data-bullet-font')).toBe('Wingdings');
    expect(solo.getAttribute('data-num-id')).not.toBe('5');
    expect(editor.querySelector('#b .list-marker')!.textContent).toBe('☒');

    const head = editor.querySelector('#a')!.parentElement!;
    const tail = editor.querySelector('#c')!.parentElement!;
    expect(head.getAttribute('data-num-id')).toBe('5');
    expect(tail.getAttribute('data-num-id')).toBe('5');
    expect(head.getAttribute('data-lvl-text')).toBe(PUA_UNCHECKED);
    expect(editor.querySelector('#a .list-marker')!.textContent).toBe('❑');
  });

  it('ponowne przełączenie wraca do ORYGINALNEGO pustego znaku (❑, nie generycznego ☐)', () => {
    checkboxList();
    caretInLi('b');
    component.toggleCheckboxBullet();

    caretInLi('b');
    component.toggleCheckboxBullet();

    const solo = editor.querySelector('#b')!.parentElement!;
    expect(solo.getAttribute('data-lvl-text')).toBe(PUA_UNCHECKED);
    expect(editor.querySelector('#b .list-marker')!.textContent).toBe('❑');
  });

  it('lista ze zwykłą kropką nie jest ruszana (punktor nie jest checkboxem)', () => {
    editor.innerHTML =
      '<ul data-num-id="9" data-ilvl="0" data-num-fmt="bullet" data-lvl-text="•">' +
      '<li id="a"><span class="list-marker">•</span>Alfa</li></ul>';
    caretInLi('a');

    component.toggleCheckboxBullet();

    expect(editor.querySelectorAll('ul').length).toBe(1);
    expect(editor.querySelector('ul')!.getAttribute('data-lvl-text')).toBe('•');
  });

  function selectItems(fromId: string, toId: string): void {
    const range = document.createRange();
    range.setStart(editor.querySelector(`#${fromId}`)!.lastChild!, 1);
    range.setEnd(editor.querySelector(`#${toId}`)!.lastChild!, 2);
    const sel = window.getSelection()!;
    sel.removeAllRanges();
    sel.addRange(range);
  }

  function click(target: Element): MouseEvent {
    const ev = new MouseEvent('click', { bubbles: true, cancelable: true });
    Object.defineProperty(ev, 'target', { value: target });
    component.onEditorClick(ev);
    return ev;
  }

  it('zaznaczenie kilku punktów przełącza je razem i skleja w JEDEN fragment nadpisania', () => {
    checkboxList();
    selectItems('a', 'b');

    component.toggleCheckboxBullet();

    const soloA = editor.querySelector('#a')!.parentElement!;
    expect(editor.querySelector('#b')!.parentElement).toBe(soloA);
    expect(soloA.getAttribute('data-lvl-text')).toBe(PUA_CHECKED);
    expect(soloA.getAttribute('data-lvl-override')).toBe('1');
    expect(editor.querySelectorAll('ul').length).toBe(2);
    expect(editor.querySelector('#c')!.parentElement!.getAttribute('data-num-id')).toBe('5');
    expect(editor.querySelector('#c .list-marker')!.textContent).toBe('❑');
  });

  it('klik w punktor listy pól wyboru odhacza TEN punkt', () => {
    checkboxList();

    const ev = click(editor.querySelector('#c .list-marker')!);

    expect(ev.defaultPrevented).toBe(true);
    expect(editor.querySelector('#c .list-marker')!.textContent).toBe('☒');
    expect(editor.querySelector('#a .list-marker')!.textContent).toBe('❑');
  });

  it('klik w punktor zwykłej listy (kropka) niczego nie zmienia', () => {
    editor.innerHTML =
      '<ul data-num-id="9" data-ilvl="0" data-num-fmt="bullet" data-lvl-text="•">' +
      '<li id="a"><span class="list-marker">•</span>Alfa</li></ul>';

    const ev = click(editor.querySelector('#a .list-marker')!);

    expect(ev.defaultPrevented).toBe(false);
    expect(editor.querySelector('ul')!.getAttribute('data-num-id')).toBe('9');
  });

  it('tryb tylko do odczytu: klik nie przełącza', () => {
    checkboxList();
    component.readOnly = true;

    click(editor.querySelector('#a .list-marker')!);

    expect(editor.querySelector('#a .list-marker')!.textContent).toBe('❑');
  });

  it('toggleCheckboxList nadaje zwykłej liście kontrakt pól wyboru (☐ w PUA Wingdings)', () => {
    editor.innerHTML = '<ul><li id="a">Alfa</li><li id="b">Beta</li></ul>';
    selectItems('a', 'b');
    (component as any).refreshListLabels = () => {};

    component.toggleCheckboxList();

    const ul = editor.querySelector('ul')!;
    expect(ul.getAttribute('data-num-id')).toMatch(/^chk-/);
    expect(ul.getAttribute('data-num-fmt')).toBe('bullet');
    expect(ul.getAttribute('data-lvl-text')).toBe(String.fromCharCode(0xf0a8));
    expect(ul.getAttribute('data-bullet-font')).toBe('Wingdings');
    expect(ul.getAttribute('data-ind-left-tw')).toBe('720');
    expect(ul.getAttribute('data-ind-hanging-tw')).toBe('360');
    expect(ul.style.listStyleType).toBe('none');
    const markers = Array.from(editor.querySelectorAll('li > span.list-marker'));
    expect(markers.map(m => m.textContent)).toEqual(['☐', '☐']);
    expect(markers.every(m => m.getAttribute('contenteditable') === 'false')).toBe(true);
  });

  it('toggleCheckboxList na liście DOCX z innym punktorem daje NOWĄ tożsamość instancji', () => {
    editor.innerHTML =
      '<ul data-num-id="9" data-abstract-num-id="4" data-ilvl="0" data-num-fmt="bullet" data-lvl-text="•">' +
      '<li id="a"><span class="list-marker" contenteditable="false">•</span>Alfa</li></ul>';
    caretInLi('a');
    (component as any).refreshListLabels = () => {};

    component.toggleCheckboxList();

    const ul = editor.querySelector('ul')!;
    expect(ul.getAttribute('data-num-id')).not.toBe('9');
    expect(ul.getAttribute('data-abstract-num-id')).toMatch(/^chk-abs-/);
    expect(editor.querySelector('#a .list-marker')!.textContent).toBe('☐');
  });

  it('Enter w pustym punkcie wychodzi z listy (akapit za listą, punkt usunięty)', () => {
    checkboxList();
    const li = editor.querySelector('#c')!;
    li.lastChild!.remove();
    const range = document.createRange();
    range.selectNodeContents(li);
    range.collapse(false);
    window.getSelection()!.removeAllRanges();
    window.getSelection()!.addRange(range);

    const handled = (component as any)._handleChecklistEnter();

    expect(handled).toBe(true);
    expect(editor.querySelector('#c')).toBeNull();
    expect(editor.lastElementChild!.tagName).toBe('P');
    expect(editor.querySelectorAll('li').length).toBe(2);
  });

  it('Enter po odhaczonym punkcie: nowy punkt startuje jako pusty ☐', () => {
    checkboxList();
    caretInLi('c');
    component.toggleCheckboxBullet();
    caretInLi('c');
    expect((component as any)._handleChecklistEnter()).toBe(false);

    const solo = editor.querySelector('#c')!.parentElement!;
    const fresh = document.createElement('li');
    fresh.id = 'd';
    fresh.appendChild(document.createElement('br'));
    solo.appendChild(fresh);
    const range = document.createRange();
    range.setStart(fresh, 0);
    range.collapse(true);
    window.getSelection()!.removeAllRanges();
    window.getSelection()!.addRange(range);

    (component as any)._uncheckNewChecklistItemAfterEnter();

    const freshList = editor.querySelector('#d')!.parentElement!;
    expect(freshList).not.toBe(editor.querySelector('#c')!.parentElement);
    expect(freshList.getAttribute('data-lvl-text')).toBe(PUA_UNCHECKED);
    expect(editor.querySelector('#d .list-marker')!.textContent).toBe('❑');
    expect(editor.querySelector('#c .list-marker')!.textContent).toBe('☒');
  });


  function guiChecklist(): void {
    editor.innerHTML = '<ul><li id="a">Alfa</li><li id="b">Beta</li><li id="c">Gamma</li></ul>';
    selectItems('a', 'c');
    (component as any).refreshListLabels = () => {};
    component.toggleCheckboxList();
  }

  function caretAtItemStart(id: string): void {
    const li = editor.querySelector(`#${id}`)!;
    const range = document.createRange();
    range.setStart(li.lastChild!, 0);
    range.collapse(true);
    const sel = window.getSelection()!;
    sel.removeAllRanges();
    sel.addRange(range);
  }

  it('Tab na początku punktu obniża poziom: własny fragment data-ilvl=1, wcięcie +720 tw, ten sam abstrakt', () => {
    guiChecklist();
    (component as any)._flushPaginateSoon = () => {};
    caretAtItemStart('b');

    expect((component as any)._handleChecklistTab(false)).toBe(true);

    const head = editor.querySelector('#a')!.parentElement!;
    const nested = editor.querySelector('#b')!.parentElement!;
    const tail = editor.querySelector('#c')!.parentElement!;
    expect(nested).not.toBe(head);
    expect(nested.parentElement).toBe(editor);
    expect(nested.getAttribute('data-ilvl')).toBe('1');
    expect(nested.getAttribute('data-ind-left-tw')).toBe('1440');
    expect(nested.style.paddingLeft).toBe('96px');
    expect(nested.getAttribute('data-abstract-num-id')).toBe(head.getAttribute('data-abstract-num-id'));
    expect(nested.getAttribute('data-lvl-text')).toBe(String.fromCharCode(0xf0a8));
    expect(tail.getAttribute('data-ilvl')).toBe('0');
    expect(tail.getAttribute('data-num-id')).toBe(head.getAttribute('data-num-id'));
    expect(editor.querySelector('#b .list-marker')!.textContent).toBe('☐');
    expect(window.getSelection()!.getRangeAt(0).startContainer.parentElement!.id).toBe('b');
  });

  it('Tab w ŚRODKU tekstu punktu nie zmienia poziomu (wołający wstawi tabulator)', () => {
    guiChecklist();
    caretInLi('b');

    expect((component as any)._handleChecklistTab(false)).toBe(false);
    expect(editor.querySelectorAll('ul').length).toBe(1);
  });

  it('Shift+Tab wraca na poziom 0 i skleja punkt z oryginalną listą', () => {
    guiChecklist();
    (component as any)._flushPaginateSoon = () => {};
    caretAtItemStart('b');
    (component as any)._handleChecklistTab(false);

    caretAtItemStart('b');
    expect((component as any)._handleChecklistTab(true)).toBe(true);

    expect(editor.querySelectorAll('ul').length).toBe(1);
    expect(Array.from(editor.querySelectorAll('li')).map(li => li.id)).toEqual(['a', 'b', 'c']);
    expect(editor.querySelector('ul')!.getAttribute('data-ilvl')).toBe('0');
  });

  it('kolejny obniżany punkt dołącza do istniejącego fragmentu poziomu 1 (jedna instancja)', () => {
    guiChecklist();
    (component as any)._flushPaginateSoon = () => {};
    caretAtItemStart('b');
    (component as any)._handleChecklistTab(false);
    caretAtItemStart('c');
    (component as any)._handleChecklistTab(false);

    const nested = editor.querySelector('#b')!.parentElement!;
    expect(editor.querySelector('#c')!.parentElement).toBe(nested);
    expect(editor.querySelectorAll('ul').length).toBe(2);
  });

  it('odhaczony punkt zachowuje ☑ przy zmianie poziomu; poziom 0 nie podnosi się dalej', () => {
    guiChecklist();
    (component as any)._flushPaginateSoon = () => {};
    click(editor.querySelector('#b .list-marker')!);
    caretAtItemStart('b');
    (component as any)._handleChecklistTab(false);

    const nested = editor.querySelector('#b')!.parentElement!;
    expect(nested.getAttribute('data-ilvl')).toBe('1');
    expect(nested.getAttribute('data-lvl-override')).toBe('1');
    expect(editor.querySelector('#b .list-marker')!.textContent).toBe('☒');

    caretAtItemStart('a');
    (component as any)._handleChecklistTab(true);
    expect(editor.querySelector('#a')!.parentElement!.getAttribute('data-ilvl')).toBe('0');
  });

  it('przycisk „Zwiększ wcięcie" w liście pól wyboru zmienia poziom (nie execCommand indent)', () => {
    guiChecklist();
    caretInLi('c');

    (component as any)._changeParagraphIndent(1);

    expect(editor.querySelector('#c')!.parentElement!.getAttribute('data-ilvl')).toBe('1');
  });

  it('import z zagnieżdżeniem (li > ul > li): Shift+Tab wyprowadza punkt za gospodarza', () => {
    const attrs = `data-abstract-num-id="3" data-num-fmt="bullet" data-lvl-text="${PUA_UNCHECKED}" data-bullet-font="Wingdings"`;
    editor.innerHTML =
      `<ul data-num-id="5" data-ilvl="0" ${attrs}><li id="a"><span class="list-marker" contenteditable="false">❑</span>Alfa` +
      `<ul data-num-id="5" data-ilvl="1" ${attrs}><li id="b"><span class="list-marker" contenteditable="false">❑</span>Beta</li>` +
      `<li id="c"><span class="list-marker" contenteditable="false">❑</span>Gamma</li></ul></li></ul>`;
    (component as any)._flushPaginateSoon = () => {};
    caretAtItemStart('b');

    (component as any)._handleChecklistTab(true);

    const top = editor.querySelector(':scope > ul')!;
    expect(Array.from(top.children).map(li => li.id)).toEqual(['a', 'b']);
    expect(editor.querySelector('#c')!.parentElement!.closest('li')!.id).toBe('b');
    expect(editor.querySelector('#a ul')).toBeNull();
  });


  it('Backspace na początku punktu zdejmuje punktor: punkt → akapit, lista ZA nim trwa (ta sama tożsamość)', () => {
    checkboxList();
    const li = editor.querySelector('#b')!;
    const range = document.createRange();
    range.setStart(li.lastChild!, 0);
    range.collapse(true);
    window.getSelection()!.removeAllRanges();
    window.getSelection()!.addRange(range);

    expect((component as any)._handleListMarkerDeletion(false)).toBe(true);

    expect(Array.from(editor.children).map(e => `${e.tagName}:${e.textContent}`))
      .toEqual(['UL:❑Alfa', 'P:Beta', 'UL:❑Gamma']);
    expect(editor.querySelector('p .list-marker')).toBeNull();
    const [head, tail] = Array.from(editor.querySelectorAll('ul'));
    expect(tail.getAttribute('data-num-id')).toBe(head.getAttribute('data-num-id'));
    const r = window.getSelection()!.getRangeAt(0);
    expect(r.startContainer.textContent).toBe('Beta');
    expect(r.startOffset).toBe(0);
  });

  it('Backspace w ŚRODKU tekstu punktu i w liście bez znacznika zostaje przy przeglądarce', () => {
    checkboxList();
    caretInLi('b');
    expect((component as any)._handleListMarkerDeletion(false)).toBe(false);

    editor.innerHTML = '<ol><li id="n">Numer</li></ol>';
    const range = document.createRange();
    range.setStart(editor.querySelector('#n')!.firstChild!, 0);
    range.collapse(true);
    window.getSelection()!.removeAllRanges();
    window.getSelection()!.addRange(range);
    expect((component as any)._handleListMarkerDeletion(false)).toBe(false);
  });

  it('Backspace zdejmuje punktor także ze ZWYKŁEJ listy DOCX (kropka) — ten sam mechanizm', () => {
    editor.innerHTML =
      '<ul data-num-id="9" data-ilvl="0" data-num-fmt="bullet" data-lvl-text="•">' +
      '<li id="a" style="margin-bottom:8pt;text-indent:-24px;" data-style-id="ListParagraph">' +
      '<span class="list-marker" contenteditable="false">•</span>Alfa</li></ul>';
    const range = document.createRange();
    range.setStart(editor.querySelector('#a')!.lastChild!, 0);
    range.collapse(true);
    window.getSelection()!.removeAllRanges();
    window.getSelection()!.addRange(range);

    expect((component as any)._handleListMarkerDeletion(false)).toBe(true);

    const p = editor.querySelector('p')!;
    expect(editor.querySelector('ul')).toBeNull();
    expect(p.textContent).toBe('Alfa');
    expect(p.style.marginBottom).toBe('8pt');
    expect(p.style.textIndent).toBe('');
    expect(p.getAttribute('data-style-id')).toBe('ListParagraph');
  });

  it('Delete na końcu punktu dociąga treść następnego (także z sąsiedniego fragmentu), bez kasowania znacznika', () => {
    checkboxList();
    caretInLi('c');
    component.toggleCheckboxBullet();
    const b = editor.querySelector('#b')!;
    const range = document.createRange();
    range.setStart(b.lastChild!, 4);
    range.collapse(true);
    window.getSelection()!.removeAllRanges();
    window.getSelection()!.addRange(range);

    expect((component as any)._handleListMarkerDeletion(true)).toBe(true);

    expect(editor.querySelector('#c')).toBeNull();
    expect(b.textContent).toBe('❑BetaGamma');
    expect(b.querySelectorAll('.list-marker').length).toBe(1);
    expect(editor.querySelectorAll('ul').length).toBe(1);
  });

  it('Delete w środku tekstu punktu zostaje przy przeglądarce', () => {
    checkboxList();
    caretInLi('a');
    expect((component as any)._handleListMarkerDeletion(true)).toBe(false);
  });


  it('schowek Worda: akapity mso-list → lista; kontrakt pól wyboru przeżywa allowlistę sanitizera', () => {
    const item = (t: string) =>
      "<p class=MsoListParagraph style='text-indent:-18.0pt;mso-list:l2 level1 lfo3'><![if !supportLists]>" +
      "<span style='font-family:Wingdings'><span style='mso-list:Ignore'>\u00A8<span style='font:7.0pt \"Times New Roman\"'>&nbsp;&nbsp;</span></span></span>" +
      `<![endif]><span style='color:red;mso-ansi-language:PL'>${t}</span><o:p></o:p></p>`;
    const html = '<html><head><style>@list l2:level1 {mso-level-number-format:bullet;}</style></head><body>' +
      `<!--StartFragment-->${item('Zadanie')}${item('Drugie')}<!--EndFragment--></body></html>`;

    const fragment: DocumentFragment = (component as any).prepareClipboardFragment(html);

    const ul = fragment.querySelector('ul')!;
    expect(fragment.querySelectorAll('p').length).toBe(0);
    expect(ul.getAttribute('data-lvl-text')).toBe(String.fromCharCode(0xf0a8));
    expect(ul.getAttribute('data-num-id')).toMatch(/^chk-/);
    expect(ul.style.listStyleType).toBe('none');
    const markers = Array.from(ul.querySelectorAll('li > span.list-marker'));
    expect(markers.map(m => m.textContent)).toEqual(['☐', '☐']);
    expect(markers[0].getAttribute('contenteditable')).toBe('false');
    const span = ul.querySelector('li span:not(.list-marker)') as HTMLElement;
    expect(span.getAttribute('style')).toBe('color:red;');
    expect((component as any)._checkboxBulletState(ul)).not.toBeNull();
  });


  it('hoist: lista zostawiona przez przeglądarkę W <p> wychodzi na poziom bloku, selekcja przeżywa', () => {
    editor.innerHTML = '<p id="p1"></p><p id="p2">Dalej</p>';
    const p1 = editor.querySelector('#p1')!;
    const ul = document.createElement('ul');
    ul.innerHTML = '<li id="a">Alfa</li><li id="b">Beta</li>';
    p1.appendChild(ul);
    selectItems('a', 'b');

    (component as any)._hoistListsOutOfParagraphs(editor);

    expect(ul.parentElement).toBe(editor);
    expect(editor.querySelector('#p1')).toBeNull();
    expect(editor.querySelector('p ul')).toBeNull();
    const r = window.getSelection()!.getRangeAt(0);
    expect(r.startContainer.parentElement!.id).toBe('a');
    expect(r.endContainer.parentElement!.id).toBe('b');
  });

  it('hoist: treść akapitu przed i za listą zostaje w osobnych akapitach', () => {
    editor.innerHTML = '<p id="p1">przed</p>';
    const p1 = editor.querySelector('#p1')!;
    const ul = document.createElement('ul');
    ul.innerHTML = '<li>punkt</li>';
    p1.appendChild(ul);
    p1.appendChild(document.createTextNode('za'));

    (component as any)._hoistListsOutOfParagraphs(editor);

    expect(Array.from(editor.children).map(e => `${e.tagName}:${e.textContent}`)).toEqual(['P:przed', 'UL:punkt', 'P:za']);
  });


  const SDT_BOX =
    '<span class="sdt-inline sdt-checkbox" contenteditable="false" data-sdt-checkbox="1"' +
    ' data-checked="0" data-checked-glyph="☑" data-unchecked-glyph="☐" data-sdt-props="UFJPUFM=">☐</span>';

  it('klik w formant pola wyboru przełącza stan i glif (definicja stanów z dokumentu)', () => {
    editor.innerHTML = `<p id="p">${SDT_BOX} Zgoda</p>`;
    const box = editor.querySelector('.sdt-checkbox')!;

    click(box);
    expect(box.getAttribute('data-checked')).toBe('1');
    expect(box.textContent).toBe('☑');
    expect(box.getAttribute('data-sdt-props')).toBe('UFJPUFM=');

    click(box);
    expect(box.getAttribute('data-checked')).toBe('0');
    expect(box.textContent).toBe('☐');
  });

  it('„Zaznacz / odznacz" poza listą przełącza formant bieżącego akapitu', () => {
    editor.innerHTML = `<p id="p">${SDT_BOX} Zgoda</p>`;
    const range = document.createRange();
    range.setStart(editor.querySelector('#p')!.lastChild!, 3);
    range.collapse(true);
    window.getSelection()!.removeAllRanges();
    window.getSelection()!.addRange(range);

    component.toggleCheckboxBullet();

    expect(editor.querySelector('.sdt-checkbox')!.getAttribute('data-checked')).toBe('1');
  });

  it('insertCheckboxControl wstawia atomowy formant w miejscu kursora + spację na karetkę', () => {
    editor.innerHTML = '<p id="p">Zgoda</p>';
    const text = editor.querySelector('#p')!.firstChild!;
    const range = document.createRange();
    range.setStart(text, 0);
    range.collapse(true);
    window.getSelection()!.removeAllRanges();
    window.getSelection()!.addRange(range);

    component.insertCheckboxControl();

    const box = editor.querySelector('#p')!.firstElementChild!;
    expect(box.classList.contains('sdt-checkbox')).toBe(true);
    expect(box.getAttribute('contenteditable')).toBe('false');
    expect(box.getAttribute('data-sdt-checkbox')).toBe('1');
    expect(box.getAttribute('data-checked')).toBe('0');
    expect(editor.querySelector('#p')!.textContent).toBe('☐ Zgoda');
    const sel = window.getSelection()!;
    expect(sel.getRangeAt(0).startContainer.nodeType).toBe(Node.TEXT_NODE);
  });

  it('raportuje stan pola pod karetką dla toolbara: punkt listy, formant w akapicie, brak pola', () => {
    if (typeof (document as any).queryCommandState !== 'function') {
      (document as any).queryCommandState = () => false;
    }
    const state = (): { list: boolean | undefined; box: string | null | undefined } => {
      (component as any).updateFormattingState();
      const formatting = component.editorState().currentFormatting;
      return { list: formatting.checkboxList, box: formatting.checkboxState };
    };

    checkboxList();
    caretInLi('b');
    expect(state()).toEqual({ list: true, box: 'unchecked' });

    component.toggleCheckboxBullet();
    caretInLi('b');
    expect(state()).toEqual({ list: true, box: 'checked' });
    caretInLi('a');
    expect(state()).toEqual({ list: true, box: 'unchecked' });

    editor.innerHTML =
      '<p id="p"><span class="sdt-inline sdt-checkbox" data-sdt-checkbox="1" data-checked="1" contenteditable="false">☒</span> Zgoda</p>' +
      '<p id="q">Bez pola</p>';
    caretInLi('p');
    expect(state()).toEqual({ list: false, box: 'checked' });
    caretInLi('q');
    expect(state()).toEqual({ list: false, box: null });
  });

  it('znak zaznaczenia: domyślnie krzyżyk, wybór ptaszka obowiązuje dla kolejnych odhaczeń i pól pod kursorem', () => {
    const marks = TestBed.inject(CheckboxMarkService);
    expect(marks.mark()).toBe('cross');

    checkboxList();
    caretInLi('a');
    component.toggleCheckboxBullet();
    expect(editor.querySelector('#a')!.parentElement!.getAttribute('data-lvl-text')).toBe(PUA_CHECKED);
    expect(editor.querySelector('#a .list-marker')!.textContent).toBe('☒');

    marks.set('check');
    caretInLi('a');
    component.applyCheckboxMark();
    expect(editor.querySelector('#a')!.parentElement!.getAttribute('data-lvl-text')).toBe(PUA_CHECKED_TICKBOX);
    expect(editor.querySelector('#a .list-marker')!.textContent).toBe('☑');
    expect(editor.querySelector('#b .list-marker')!.textContent).toBe('❑');

    caretInLi('c');
    component.toggleCheckboxBullet();
    expect(editor.querySelector('#c .list-marker')!.textContent).toBe('☑');
    caretInLi('c');
    component.toggleCheckboxBullet();
    expect(editor.querySelector('#c .list-marker')!.textContent).toBe('❑');
    expect(localStorage.getItem('d2.editor.checkboxMark')).toBe('check');
  });

  it('znak zaznaczenia w pojedynczym polu: nowe pole niesie wybór, zmiana znaku przestawia pole pod kursorem', () => {
    const marks = TestBed.inject(CheckboxMarkService);
    marks.set('tick');

    const created = (component as any)._createSdtCheckbox(true) as HTMLElement;
    expect(created.getAttribute('data-checked-mark')).toBe('tick');
    expect(created.getAttribute('data-checked-glyph')).toBe('✔');
    expect(created.textContent).toBe('✔');

    editor.innerHTML =
      '<p id="p"><span class="sdt-inline sdt-checkbox" data-sdt-checkbox="1" data-checked="1" data-checked-glyph="☑"'
      + ' data-unchecked-glyph="☐" contenteditable="false">☑</span> Zgoda</p>'
      + '<p id="q"><span class="sdt-inline sdt-checkbox" data-sdt-checkbox="1" data-checked="0" data-checked-glyph="☑"'
      + ' data-unchecked-glyph="☐" contenteditable="false">☐</span> Inna zgoda</p>';

    marks.set('cross');
    caretInLi('p');
    component.applyCheckboxMark();

    const first = editor.querySelector('#p .sdt-checkbox')!;
    expect(first.getAttribute('data-checked-mark')).toBe('cross');
    expect(first.textContent).toBe('☒');
    const second = editor.querySelector('#q .sdt-checkbox')!;
    expect(second.hasAttribute('data-checked-mark')).toBe(false);
    expect(second.getAttribute('data-checked-glyph')).toBe('☑');
  });

  it('ensureBulletMarkers syntetyzuje marker z kontraktu, gdy nie ma wzorca w kontenerze', () => {
    editor.innerHTML =
      `<ul data-num-id="5" data-ilvl="0" data-num-fmt="bullet"` +
      ` data-lvl-text="${PUA_UNCHECKED}" data-bullet-font="Wingdings">` +
      '<li>bez markera</li></ul>';

    ensureBulletMarkers([editor]);

    const marker = editor.querySelector<HTMLElement>('li > span.list-marker');
    expect(marker).not.toBeNull();
    expect(marker!.textContent).toBe('❑');
    expect(marker!.getAttribute('contenteditable')).toBe('false');
  });

  it('bulletGlyphFromContract mapuje PUA bez fontu i glify wprost', () => {
    expect(bulletGlyphFromContract(PUA_CHECKED, null)).toBe('☒');
    expect(bulletGlyphFromContract(PUA_CHECKED_TICKBOX, null)).toBe('☑');
    expect(bulletGlyphFromContract(String.fromCharCode(0xf0a8), 'Wingdings')).toBe('☐');
    expect(bulletGlyphFromContract('▪', 'Arial')).toBe('▪');
    expect(bulletGlyphFromContract(String.fromCharCode(0xf099), null)).toBe('•');
  });
});
