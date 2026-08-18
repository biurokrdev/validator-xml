import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — schowek', () => {
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
  });

  afterEach(() => editor.remove());

  function selectNodes(start: Node, end: Node): Range {
    const range = document.createRange();
    range.setStartBefore(start);
    range.setEndAfter(end);
    const sel = window.getSelection()!;
    sel.removeAllRanges();
    sel.addRange(range);
    return range;
  }

  function fakeClipboardEvent(): { event: ClipboardEvent; data: Map<string, string> } {
    const data = new Map<string, string>();
    const event = {
      preventDefault: () => {},
      clipboardData: {
        setData: (type: string, value: string) => data.set(type, value),
        getData: (type: string) => data.get(type) ?? '',
      },
    } as unknown as ClipboardEvent;
    return { event, data };
  }

  describe('kopiowanie', () => {
    it('częściowe zaznaczenie listy odzyskuje kontener z atrybutami numeracji', () => {
      editor.innerHTML =
        '<ol data-num-fmt="decimal" data-start="5" style="padding-left:36px;">' +
        '<li id="a">Alfa</li><li id="b">Beta</li><li id="c">Gamma</li></ol>';
      const range = selectNodes(editor.querySelector('#a')!, editor.querySelector('#b')!);

      const html = (component as any).buildClipboardHtml(range) as string;

      expect(html).toContain('data-d2-clip="1"');
      expect(html).toContain('<ol data-num-fmt="decimal" data-start="5"');
      expect(html).toContain('Alfa');
      expect(html).toContain('Beta');
      expect(html).not.toContain('Gamma');
    });

    it('częściowe zaznaczenie tabeli odzyskuje <table> z colgroup (szerokości kolumn)', () => {
      editor.innerHTML =
        '<table data-tbl-w="auto" style="border-collapse:collapse;">' +
        '<colgroup><col style="width:100px;"><col style="width:200px;"></colgroup>' +
        '<tbody><tr id="r1"><td>A1</td><td>B1</td></tr><tr id="r2"><td>A2</td><td>B2</td></tr></tbody></table>';
      const range = selectNodes(editor.querySelector('#r1')!, editor.querySelector('#r1')!);

      const html = (component as any).buildClipboardHtml(range) as string;

      expect(html).toContain('<table data-tbl-w="auto"');
      expect(html).toContain('<colgroup>');
      expect(html).toContain('A1');
      expect(html).not.toContain('A2');
    });

    it('zaznaczenie od akapitu w głąb listy odzyskuje powłokę z kontraktem (nie gołe <ul>)', () => {
      editor.innerHTML =
        '<p id="p">akapit</p>' +
        '<ul data-num-id="7" data-num-fmt="bullet" style="margin:0;padding-left:48px;">' +
        '<li id="a">Alfa</li><li>Beta</li></ul>';
      const range = selectNodes(editor.querySelector('#p')!, editor.querySelector('#a')!);

      const html = (component as any).buildClipboardHtml(range) as string;

      expect(html).toContain('data-num-id="7"');
      expect(html).toContain('padding-left:48px');
      expect(html).toContain('Alfa');
      expect(html).not.toContain('Beta');
    });

    it('markery podziału paginacji i zakładki NIE wchodzą do schowka', () => {
      editor.innerHTML =
        '<table data-split-table-id="s1"><tbody><tr id="r"><td>X' +
        '<span class="docx-bookmark" data-bm-name="cel"></span></td></tr></tbody></table>';
      const range = selectNodes(editor.querySelector('#r')!, editor.querySelector('#r')!);

      const html = (component as any).buildClipboardHtml(range) as string;

      expect(html).not.toContain('data-split-table-id');
      expect(html).not.toContain('docx-bookmark');
      expect(html).toContain('X');
    });
  });

  describe('wklejanie — sanitizacja zewnętrzna (Word/WWW)', () => {
    function prepared(html: string): HTMLElement {
      const fragment = (component as any).prepareClipboardFragment(html) as DocumentFragment;
      const probe = document.createElement('div');
      probe.appendChild(fragment);
      return probe;
    }

    it('zdejmuje klasy Mso, style mso-* i marginesy Worda; zostawia bold/kolor', () => {
      const probe = prepared(
        '<p class="MsoNormal" style="margin:0cm 0cm 8pt;mso-line-height-alt:12pt;color:#c00;">' +
        '<b style="mso-bidi-font-weight:normal;">Ważne</b></p>');

      const p = probe.querySelector('p')!;
      expect(p.getAttribute('class')).toBeNull();
      expect(p.getAttribute('style')).toContain('color:#c00');
      expect(p.getAttribute('style')).not.toContain('mso');
      expect(p.getAttribute('style')).not.toContain('margin');
      expect(probe.querySelector('b')!.textContent).toBe('Ważne');
    });

    it('odpakowuje nieznane tagi (o:p, font), usuwa skrypty i zdalne obrazki', () => {
      const probe = prepared(
        '<div><o:p><font face="Arial">Tekst</font></o:p>' +
        '<script>alert(1)</script><img src="https://evil/x.png"><img src="data:image/png;base64,AA=="></div>');

      expect(probe.textContent).toContain('Tekst');
      expect(probe.querySelector('font')).toBeNull();
      expect(probe.querySelector('script')).toBeNull();
      expect(probe.querySelectorAll('img').length).toBe(1);
      expect(probe.querySelector('img')!.getAttribute('src')).toContain('data:image/png');
    });

    it('normalizuje międzywyrazowe twarde spacje (rozjazdy w tekście z Worda)', () => {
      const probe = prepared('<p>słowo po słowie i dalej</p>');

      expect(probe.textContent).toBe('słowo po słowie i dalej');
    });

    it('wklejka WEWNĘTRZNA (data-d2-clip) zachowuje kontrakty data-* edytora', () => {
      const probe = prepared(
        '<div data-d2-clip="1"><ol data-num-fmt="decimal" data-ind-left-tw="720">' +
        '<li data-list-label="1.">Punkt</li></ol></div>');

      const ol = probe.querySelector('ol')!;
      expect(ol.getAttribute('data-num-fmt')).toBe('decimal');
      expect(ol.getAttribute('data-ind-left-tw')).toBe('720');
      expect(probe.querySelector('li')!.getAttribute('data-list-label')).toBe('1.');
    });

    it('markery podziału są zdejmowane także przy wklejce (stare kopie sprzed zmiany)', () => {
      const probe = prepared(
        '<div data-d2-clip="1"><table data-split-table-id="s9"><tbody><tr><td>X</td></tr></tbody></table></div>');

      expect(probe.querySelector('[data-split-table-id]')).toBeNull();
    });
  });

  describe('wklejanie — block-aware insert', () => {
    function caretIn(textNode: Node, offset: number): void {
      const range = document.createRange();
      range.setStart(textNode, offset);
      range.collapse(true);
      const sel = window.getSelection()!;
      sel.removeAllRanges();
      sel.addRange(range);
    }

    it('tabela wklejona w środek akapitu dzieli go — bez <table> wewnątrz <p>', () => {
      editor.innerHTML = '<p style="color:#333;">poczatekkoniec</p>';
      caretIn(editor.querySelector('p')!.firstChild!, 8);

      const fragment = (component as any).prepareClipboardFragment(
        '<div data-d2-clip="1"><table><tbody><tr><td>T</td></tr></tbody></table></div>');
      (component as any).insertClipboardFragment(fragment);

      expect(editor.querySelector('p table')).toBeNull();
      const kids = Array.from(editor.children).map(el => el.tagName);
      expect(kids).toEqual(['P', 'TABLE', 'P']);
      expect(editor.children[0].textContent).toBe('poczatek');
      expect(editor.children[2].textContent).toBe('koniec');
      expect((editor.children[2] as HTMLElement).getAttribute('style')).toContain('color:#333');
    });

    it('wiodący inline dokleja się do pierwszej połówki, bloki między połówki', () => {
      editor.innerHTML = '<p>ABCD</p>';
      caretIn(editor.querySelector('p')!.firstChild!, 2);

      const fragment = (component as any).prepareClipboardFragment(
        '<div data-d2-clip="1"><span>-x-</span><p>nowy</p></div>');
      (component as any).insertClipboardFragment(fragment);

      const kids = Array.from(editor.children).map(el => el.tagName);
      expect(kids).toEqual(['P', 'P', 'P']);
      expect(editor.children[0].textContent).toBe('AB-x-');
      expect(editor.children[1].textContent).toBe('nowy');
      expect(editor.children[2].textContent).toBe('CD');
    });

    it('fragment czysto inline wchodzi w miejsce kursora bez dzielenia akapitu', () => {
      editor.innerHTML = '<p>ABCD</p>';
      caretIn(editor.querySelector('p')!.firstChild!, 2);

      const fragment = (component as any).prepareClipboardFragment(
        '<div data-d2-clip="1"><span style="font-weight:bold;">X</span></div>');
      (component as any).insertClipboardFragment(fragment);

      expect(editor.children.length).toBe(1);
      expect(editor.querySelector('p')!.textContent).toBe('ABXCD');
    });

    it('blok wklejony z kursorem w komórce NIE rozcina TD (bez dodatkowej komórki w wierszu)', () => {
      editor.innerHTML = '<table><tbody><tr><td id="c">przedpo</td><td>obok</td></tr></tbody></table>';
      caretIn(editor.querySelector('#c')!.firstChild!, 5);

      const fragment = (component as any).prepareClipboardFragment(
        '<div data-d2-clip="1"><p>wklejka</p></div>');
      (component as any).insertClipboardFragment(fragment);

      expect(editor.querySelectorAll('td').length).toBe(2);
      const cell = editor.querySelector('#c')!;
      expect(cell.querySelector('p')!.textContent).toBe('wklejka');
      expect(cell.textContent).toBe('przedwklejkapo');
    });

    it('blok wklejony w DIV (sdt-block) nie klonuje kontenera — bez duplikacji formantu', () => {
      editor.innerHTML = '<div class="sdt-block" data-sdt-id="s1"><p id="p">treść</p></div>';
      caretIn(editor.querySelector('#p')!.firstChild!, 3);

      const fragment = (component as any).prepareClipboardFragment(
        '<div data-d2-clip="1"><table><tbody><tr><td>T</td></tr></tbody></table></div>');
      (component as any).insertClipboardFragment(fragment);

      expect(editor.querySelectorAll('[data-sdt-id]').length).toBe(1);
      const sdt = editor.querySelector('[data-sdt-id]')!;
      expect(sdt.querySelector('table')).not.toBeNull();
      expect(sdt.querySelector('p table')).toBeNull();
    });

    it('lista wklejona z kursorem w li scala się do listy-gospodarza (bez ul w li = bez podwójnego wcięcia)', () => {
      editor.innerHTML =
        '<ul data-num-id="3" style="margin:0;padding-left:24px;">' +
        '<li id="host">poczatekkoniec</li></ul>';
      caretIn(editor.querySelector('#host')!.firstChild!, 8);

      const fragment = (component as any).prepareClipboardFragment(
        '<div data-d2-clip="1"><ul data-num-id="9" style="padding-left:48px;">' +
        '<li>Nowy1</li><li>Nowy2</li></ul></div>');
      (component as any).insertClipboardFragment(fragment);

      expect(editor.querySelector('li ul')).toBeNull();
      expect(editor.querySelectorAll('ul').length).toBe(1);
      const items = Array.from(editor.querySelectorAll('li')).map(li => li.textContent);
      expect(items).toEqual(['poczatek', 'Nowy1', 'Nowy2', 'koniec']);
      const ul = editor.querySelector('ul')!;
      expect(ul.getAttribute('data-num-id')).toBe('3');
      expect(ul.getAttribute('style')).toContain('padding-left:24px');
    });

    it('lista wklejona na końcu li nie zostawia pustej drugiej połówki (także z markerem punktora)', () => {
      editor.innerHTML =
        '<ul data-num-id="3"><li id="host"><span class="list-marker" contenteditable="false">●</span>tekst</li></ul>';
      const textNode = editor.querySelector('#host')!.lastChild!;
      caretIn(textNode, 5);

      const fragment = (component as any).prepareClipboardFragment(
        '<div data-d2-clip="1"><ul><li>Nowy</li></ul></div>');
      (component as any).insertClipboardFragment(fragment);

      const items = Array.from(editor.querySelectorAll('li')).map(li => li.textContent);
      expect(items).toEqual(['●tekst', 'Nowy']);
    });

    it('wklejka na końcu akapitu nie zostawia pustej drugiej połówki', () => {
      editor.innerHTML = '<p>ABCD</p>';
      caretIn(editor.querySelector('p')!.firstChild!, 4);

      const fragment = (component as any).prepareClipboardFragment(
        '<div data-d2-clip="1"><p>nowy</p></div>');
      (component as any).insertClipboardFragment(fragment);

      const kids = Array.from(editor.children).map(el => el.textContent);
      expect(kids).toEqual(['ABCD', 'nowy']);
    });
  });

  describe('wytnij', () => {
    it('cut zapisuje payload i usuwa zaznaczenie', () => {
      editor.innerHTML = '<p><span id="s1">usuń</span><span id="s2">zostaw</span></p>';
      selectNodes(editor.querySelector('#s1')!, editor.querySelector('#s1')!);
      const { event, data } = fakeClipboardEvent();

      (component as any).handleCopyOrCut(event, true);

      expect(data.get('text/html')).toContain('usuń');
      expect(data.get('text/plain')).toBe('usuń');
      expect(editor.textContent).toBe('zostaw');
    });

    it('copy nie modyfikuje dokumentu', () => {
      editor.innerHTML = '<p><span id="s1">tekst</span></p>';
      selectNodes(editor.querySelector('#s1')!, editor.querySelector('#s1')!);
      const { event, data } = fakeClipboardEvent();

      (component as any).handleCopyOrCut(event, false);

      expect(data.get('text/html')).toContain('data-d2-clip');
      expect(editor.textContent).toBe('tekst');
    });
  });
});
