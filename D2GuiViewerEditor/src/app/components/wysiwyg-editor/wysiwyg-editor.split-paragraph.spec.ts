import { TestBed, ComponentFixture } from '@angular/core/testing';
import { ElementRef } from '@angular/core';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — podział akapitu na granicy strony (ADR-0046)', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;

  beforeEach(async () => {
    if (typeof (document as any).queryCommandState !== 'function') {
      (document as any).queryCommandState = () => false;
    }
    await TestBed.configureTestingModule({ imports: [WysiwygEditorComponent] }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;
  });

  const hostEls: HTMLElement[] = [];
  afterEach(() => {
    hostEls.splice(0).forEach(el => el.remove());
    window.getSelection()?.removeAllRanges();
  });

  function editorsWith(...htmls: string[]): HTMLDivElement[] {
    const editors = htmls.map(html => {
      const editor = document.createElement('div');
      editor.innerHTML = html;
      document.body.appendChild(editor);
      hostEls.push(editor);
      return editor;
    });
    (component as any).pageEditorRefs = {
      toArray: () => editors.map(e => new ElementRef(e)),
    };
    return editors;
  }

  function stubBlockHeights(): void {
    (component as any)._measureBlockRunHeights = (_m: HTMLElement, blocks: HTMLElement[]) =>
      blocks.map(b => parseFloat(b.getAttribute('data-h') ?? '0') || 0);
  }

  function stubSplitter(): void {
    (component as any)._splitBlockAtBudget = (block: HTMLElement, budgetPx: number) => {
      const total = parseFloat(block.getAttribute('data-h') ?? '0');
      if (block.tagName !== 'P' || total <= budgetPx || budgetPx < 50) return null;
      const text = block.textContent ?? '';
      const keep = Math.max(1, Math.floor((text.length * budgetPx) / total));
      if (keep >= text.length) return null;
      block.textContent = text.slice(0, keep);
      block.setAttribute('data-h', String(budgetPx));
      const cont = document.createElement('p');
      cont.textContent = text.slice(keep);
      cont.setAttribute('data-h', String(total - budgetPx));
      cont.setAttribute('data-split-para', 'cont');
      cont.style.marginTop = '0';
      return [block, cont];
    };
  }


  it('akapit przekraczający resztę strony dzieli się: fragment zostaje, kontynuacja idzie dalej', () => {
    (component as any)._captureDocumentDefaults('<p>x</p>');
    const text = 'x'.repeat(1200);
    editorsWith(`<p data-h="400">${'a'.repeat(400)}</p><p data-h="1200">${text}</p>`);
    stubBlockHeights();
    stubSplitter();

    (component as any)._repaginateNow();

    const pages = component.pageContents();
    expect(pages.length).toBe(2);
    expect(pages[0]).not.toContain('data-split-para');
    expect(pages[1]).toContain('data-split-para="cont"');
    const tmp = document.createElement('div');
    tmp.innerHTML = pages.join('');
    expect((tmp.textContent ?? '').replace(/[^x]/g, '').length).toBe(1200);
  });

  it('bardzo długi akapit dzieli się wielokrotnie przez kolejne strony', () => {
    (component as any)._captureDocumentDefaults('<p>x</p>');
    editorsWith(`<p data-h="2400">${'y'.repeat(2400)}</p>`);
    stubBlockHeights();
    stubSplitter();

    (component as any)._repaginateNow();

    const pages = component.pageContents();
    expect(pages.length).toBe(3);
    expect(pages[1]).toContain('data-split-para="cont"');
    expect(pages[2]).toContain('data-split-para="cont"');
    const tmp = document.createElement('div');
    tmp.innerHTML = pages.join('');
    expect((tmp.textContent ?? '').replace(/[^y]/g, '').length).toBe(2400);
  });

  it('blok niedzielny zachowuje stare zachowanie (w całości na kolejną stronę)', () => {
    (component as any)._captureDocumentDefaults('<p>x</p>');
    editorsWith('<p data-h="400">A</p><p data-h="800">B</p>');
    stubBlockHeights(); // realny _splitBlockAtBudget w jsdom → null (brak layoutu)

    (component as any)._repaginateNow();

    const pages = component.pageContents();
    expect(pages.length).toBe(2);
    expect(pages[0]).toContain('>A</p>');
    expect(pages[1]).toContain('>B</p>');
    expect(pages.join('')).not.toContain('data-split-para');
  });


  it('getContent scala fragmenty z powrotem w jeden akapit (bez data-split-para)', () => {
    (component as any)._captureDocumentDefaults('<p>x</p>');
    editorsWith(
      '<p style="text-align:justify;">Abc </p>',
      '<p style="text-align:justify;margin-top: 0px;" data-split-para="cont">def</p>'
    );

    const content = component.getContent();
    expect(content).not.toContain('data-split-para');
    expect(content).toMatch(/<p[^>]*>Abc def<\/p>/);
  });

  it('osierocony fragment kontynuacji zostaje zwykłym akapitem (bez utraty treści)', () => {
    (component as any)._captureDocumentDefaults('<p>x</p>');
    editorsWith('<table><tr><td>T</td></tr></table><p data-split-para="cont">def</p>');

    const content = component.getContent();
    expect(content).not.toContain('data-split-para');
    expect(content).toContain('>def</p>');
  });

  it('repaginacja scala fragmenty PRZED układaniem (fragmentacja nie utrwala się)', () => {
    (component as any)._captureDocumentDefaults('<p>x</p>');
    editorsWith(
      '<p data-h="300">Abc </p>',
      '<p data-h="300" data-split-para="cont">def</p>'
    );
    stubBlockHeights();

    (component as any)._repaginateNow();

    const pages = component.pageContents();
    expect(pages.length).toBe(1);
    expect(pages[0]).toContain('>Abc def</p>');
    expect(pages[0]).not.toContain('data-split-para');
  });


  function stubListItemBottoms(perItemPx = 100): void {
    (component as any)._measureListItemBottoms = (list: HTMLElement) =>
      (Array.from(list.children) as HTMLElement[]).map((_li, i) => (i + 1) * perItemPx);
  }

  it('lista dłuższa niż reszta strony dzieli się między punktami (li atomowe)', () => {
    (component as any)._captureDocumentDefaults('<p>x</p>');
    editorsWith(
      '<p data-h="700">intro</p>' +
      '<ul data-h="600"><li>a</li><li>b</li><li>c</li><li>d</li><li>e</li><li>f</li></ul>'
    );
    stubBlockHeights();
    stubListItemBottoms(); // 100 px/punkt → w ~233 px resztki mieszczą się 2 punkty

    (component as any)._repaginateNow();

    const pages = component.pageContents();
    expect(pages.length).toBe(2);
    expect(pages[0]).toContain('<li>b</li>');
    expect(pages[0]).not.toContain('<li>c</li>');
    expect(pages[1]).toMatch(/<ul[^>]*data-split-para="cont"/);
    expect(pages[1]).toContain('<li>c</li>');
    expect(pages[1]).toContain('<li>f</li>');
  });

  it('kontynuacja natywnego <ol> dostaje start (numeracja nie restartuje)', () => {
    const ol = document.createElement('ol');
    ol.innerHTML = '<li>1</li><li>2</li><li>3</li><li>4</li><li>5</li>';
    stubListItemBottoms();
    const measurer = document.createElement('div');

    const parts = (component as any)._splitListBetweenItems(ol, 250, measurer);
    expect(parts).not.toBeNull();
    const [head, cont] = parts as [HTMLElement, HTMLElement];
    expect(head.children.length).toBe(2);
    expect(cont.children.length).toBe(3);
    expect(cont.getAttribute('start')).toBe('3');
    expect(cont.getAttribute('data-split-para')).toBe('cont');
  });

  it('lista DOCX (data-num-id) NIE dostaje start — numeruje silnik etykiet', () => {
    const ol = document.createElement('ol');
    ol.setAttribute('data-num-id', '7');
    ol.innerHTML = '<li>1</li><li>2</li><li>3</li>';
    stubListItemBottoms();

    const parts = (component as any)._splitListBetweenItems(ol, 150, document.createElement('div'));
    expect(parts).not.toBeNull();
    const cont = (parts as [HTMLElement, HTMLElement])[1];
    expect(cont.hasAttribute('start')).toBe(false);
    expect(cont.getAttribute('data-num-id')).toBe('7');
  });

  it('getContent scala fragmenty listy w jedną listę', () => {
    (component as any)._captureDocumentDefaults('<p>x</p>');
    editorsWith(
      '<ul><li>a</li><li>b</li></ul>',
      '<ul data-split-para="cont" style="margin-top: 0px;"><li>c</li></ul>'
    );

    const content = component.getContent();
    expect(content).not.toContain('data-split-para');
    expect((content.match(/<ul/g) ?? []).length).toBe(1);
    expect(content).toContain('<li>c</li>');
  });


  it('tabela niemieszcząca się w resztce strony idzie na następną (nie wjeżdża pod stopkę)', () => {
    (component as any)._captureDocumentDefaults('<p>x</p>');
    editorsWith(
      '<p data-h="800">A</p>' +
      '<table data-h="200"><tbody><tr><td>Nr ref.</td></tr></tbody></table>'
    );
    stubBlockHeights();
    (component as any)._splitTableForPagination = (t: HTMLTableElement) => [t];

    (component as any)._repaginateNow();

    const pages = component.pageContents();
    expect(pages.length).toBe(2);
    expect(pages[0]).toContain('>A</p>');
    expect(pages[0]).not.toContain('<table');
    expect(pages[1]).toContain('<table');
  });

  it('tabela wyższa niż pusta strona ląduje na świeżej stronie bez pętli (guard anty-pętla)', () => {
    (component as any)._captureDocumentDefaults('<p>x</p>');
    editorsWith(
      '<p data-h="100">A</p>' +
      '<table data-h="2000"><tbody><tr><td>Wielka</td></tr></tbody></table>'
    );
    stubBlockHeights();
    (component as any)._splitTableForPagination = (t: HTMLTableElement) => [t];

    (component as any)._repaginateNow();

    const pages = component.pageContents();
    expect(pages.length).toBe(2);
    expect(pages[1]).toContain('<table');
  });


  it('pierwszy fragment mieszczący się w resztce strony ZOSTAJE na niej (bez ucieczki na następną)', () => {
    (component as any)._captureDocumentDefaults('<p>x</p>');
    editorsWith(
      '<p data-h="400">A</p>' +
      '<table><tbody><tr><td>T1</td></tr><tr><td>T2</td></tr></tbody></table>'
    );
    stubBlockHeights();
    (component as any)._splitTableForPagination = () => {
      const mk = (h: number, txt: string) => {
        const t = document.createElement('table');
        t.setAttribute('data-h', String(h));
        t.innerHTML = `<tbody><tr><td>${txt}</td></tr></tbody>`;
        return t;
      };
      return [mk(400, 'T1'), mk(500, 'T2')];
    };

    (component as any)._repaginateNow();

    const pages = component.pageContents();
    expect(pages.length).toBe(2);
    expect(pages[0]).toContain('>A</p>');
    expect(pages[0]).toContain('T1');
    expect(pages[0]).not.toContain('T2');
    expect(pages[1]).toContain('T2');
    expect(pages[1]).not.toContain('T1');
  });

  it('fragmenty skrojone pod starą resztkę są po przejściu na świeżą stronę cięte OD NOWA pełnym budżetem (RC2)', () => {
    (component as any)._captureDocumentDefaults('<p>x</p>');
    editorsWith(
      '<p data-h="800">A</p>' +
      '<table><tbody><tr><td>Big</td></tr></tbody></table>'
    );
    stubBlockHeights();
    const calls: Array<{ first: number; full: number }> = [];
    const mk = (h: number, txt: string) => {
      const t = document.createElement('table');
      t.setAttribute('data-h', String(h));
      t.innerHTML = `<tbody><tr><td>${txt}</td></tr></tbody>`;
      return t;
    };
    (component as any)._splitTableForPagination =
      (_t: HTMLTableElement, first: number, full: number) => {
        calls.push({ first, full });
        return calls.length === 1 ? [mk(200, 'STALE0'), mk(800, 'STALE1')] : [mk(900, 'FRESH')];
      };

    (component as any)._repaginateNow();

    const pages = component.pageContents();
    expect(calls.length).toBe(2);
    expect(calls[1].first).toBe(calls[1].full); // re-split pełnym budżetem świeżej kolumny
    expect(calls[1].first).toBeGreaterThan(calls[0].first);
    expect(pages.length).toBe(2);
    expect(pages[1]).toContain('FRESH');
    expect(pages.join('')).not.toContain('STALE');
  });


  function stubChildEdges(perChildPx = 300): void {
    (component as any)._measureChildEdges = (container: HTMLElement) =>
      (Array.from(container.children) as HTMLElement[]).map((_c, i) => ({
        top: i * perChildPx,
        bottom: (i + 1) * perChildPx,
      }));
  }

  it('sdt-block dłuższy niż reszta strony dzieli się między dziećmi (bez dziury na pół strony)', () => {
    (component as any)._captureDocumentDefaults('<p>x</p>');
    editorsWith(
      '<p data-h="400">intro</p>' +
      '<div class="sdt-block" data-sdt-props="UFJPUFM=" data-h="1200">' +
      '<p>a</p><p>b</p><p>c</p><p>d</p></div>'
    );
    stubBlockHeights();
    stubChildEdges(); // 300 px/dziecko → w ~533 px resztki mieści się 1 dziecko

    (component as any)._repaginateNow();

    const pages = component.pageContents();
    expect(pages.length).toBe(2);
    expect(pages[0]).toContain('<p>a</p>');
    expect(pages[0]).not.toContain('<p>c</p>');
    expect(pages[1]).toMatch(/<div[^>]*data-split-para="cont"/);
    expect(pages[1]).toContain('class="sdt-block"');
    expect(pages[1]).toContain('data-sdt-props="UFJPUFM="');
    expect(pages[1]).toContain('<p>d</p>');
  });

  it('dziecko graniczne SDT tnie się rekurencyjnie po liniach (fragment akapitu zostaje na stronie)', () => {
    const sdt = document.createElement('div');
    sdt.className = 'sdt-block';
    sdt.setAttribute('data-sdt-props', 'UFJPUFM=');
    sdt.innerHTML = '<p>pierwszy</p><p>graniczny-akapit</p><p>ostatni</p>';
    stubChildEdges(); // dzieci: 0-300, 300-600, 600-900
    (component as any)._splitBlockAtBudget = (block: HTMLElement, budgetPx: number) => {
      if (block.tagName !== 'P') return null;
      const text = block.textContent ?? '';
      block.textContent = text.slice(0, 5);
      const cont = document.createElement('p');
      cont.textContent = text.slice(5);
      cont.setAttribute('data-split-para', 'cont');
      return [block, cont];
    };

    const parts = (component as any)._splitSdtBetweenChildren(
      sdt, 450, document.createElement('div'), 20);

    expect(parts).not.toBeNull();
    const [head, cont] = parts as [HTMLElement, HTMLElement];
    expect(head.textContent).toBe('pierwszygrani');
    expect(cont.textContent).toBe('czny-akapitostatni');
    expect(cont.classList.contains('sdt-block')).toBe(true);
    expect(cont.getAttribute('data-split-para')).toBe('cont');
  });

  it('getContent scala fragmenty SDT w JEDEN formant (writer nie może dostać dwóch)', () => {
    (component as any)._captureDocumentDefaults('<p>x</p>');
    editorsWith(
      '<div class="sdt-block" data-sdt-props="UFJPUFM="><p>a</p><p>b</p></div>',
      '<div class="sdt-block" data-sdt-props="UFJPUFM=" data-split-para="cont" style="margin-top: 0px;"><p>c</p></div>'
    );

    const content = component.getContent();
    expect(content).not.toContain('data-split-para');
    expect((content.match(/sdt-block/g) ?? []).length).toBe(1);
    expect(content).toContain('<p>c</p>');
  });

  it('osierocony fragment SDT nie wlewa się w obcy div (np. marker sekcji)', () => {
    (component as any)._captureDocumentDefaults('<p>x</p>');
    editorsWith(
      '<div class="docx-section-break" data-break-type="nextPage"></div>' +
      '<div class="sdt-block" data-sdt-props="UFJPUFM=" data-split-para="cont"><p>tresc</p></div>'
    );

    const content = component.getContent();
    expect(content).not.toContain('data-split-para');
    const tmp = document.createElement('div');
    tmp.innerHTML = content;
    expect(tmp.querySelector('.docx-section-break')!.textContent).toBe('');
    expect(tmp.querySelector('.sdt-block')!.textContent).toBe('tresc');
  });

  it('lista z tab-segiem w punkcie dzieli się MIĘDZY punktami (guard tab-segów nie blokuje list)', () => {
    const ol = document.createElement('ol');
    ol.innerHTML =
      '<li><span class="docx-tab-seg">1)</span>aaa</li><li>bbb</li><li>ccc</li><li>ddd</li>';
    stubListItemBottoms();

    const parts = (component as any)._splitBlockAtBudget(
      ol, 250, document.createElement('div'), 20);

    expect(parts).not.toBeNull();
    const [head, cont] = parts as [HTMLElement, HTMLElement];
    expect(head.children.length).toBe(2);
    expect(cont.children.length).toBe(2);
  });


  it('kotwica karetki w fragmencie kontynuacji wskazuje akapit LOGICZNY z globalnym offsetem', () => {
    const [, ed2] = editorsWith('<p>Hello </p>', '<p data-split-para="cont">world</p>');
    const textNode = ed2.querySelector('p')!.firstChild as Text;
    const range = document.createRange();
    range.setStart(textNode, 3);
    range.collapse(true);

    const caret = (component as any)._globalCaretFromRange(
      range, (component as any).pageEditorRefs.toArray());
    expect(caret).toEqual({ block: 0, offset: 'Hello '.length + 3 });
  });

  it('restore karetki schodzi łańcuchem fragmentów do właściwego kawałka', () => {
    const [, ed2] = editorsWith('<p>Hello </p>', '<p data-split-para="cont">world</p>');

    (component as any)._restoreGlobalCaret({ block: 0, offset: 'Hello '.length + 3 });

    const sel = window.getSelection()!;
    expect(sel.rangeCount).toBe(1);
    const r = sel.getRangeAt(0);
    expect(ed2.contains(r.startContainer)).toBe(true);
    expect(r.startOffset).toBe(3);
  });

  it('kotwica bez fragmentów działa jak dotąd (regresja zwykłych dokumentów)', () => {
    const [ed1] = editorsWith('<p>abc</p><p>defgh</p>');
    const textNode = ed1.querySelectorAll('p')[1].firstChild as Text;
    const range = document.createRange();
    range.setStart(textNode, 2);
    range.collapse(true);

    const caret = (component as any)._globalCaretFromRange(
      range, (component as any).pageEditorRefs.toArray());
    expect(caret).toEqual({ block: 1, offset: 2 });

    (component as any)._restoreGlobalCaret(caret);
    const r = window.getSelection()!.getRangeAt(0);
    expect(ed1.querySelectorAll('p')[1].contains(r.startContainer)).toBe(true);
  });
});
