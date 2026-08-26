import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';
import { HeaderFooterContent } from '../../models/document.model';

describe('WysiwygEditorComponent — variant editing routing', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [WysiwygEditorComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;
  });

  function inputEvent(html: string): Event {
    return { target: { innerHTML: html } } as unknown as Event;
  }

  const hf = (overrides: Partial<HeaderFooterContent>): HeaderFooterContent => ({
    html: 'DEFAULT', height: 1.27, ...overrides
  });

  it('writes default header content to _headerHtml when no variant is active', () => {
    component.headerContent = hf({ html: 'DEFAULT' });

    component.onHeaderInput(inputEvent('EDITED'));

    expect((component as any)._headerHtml()).toBe('EDITED');
    expect((component as any)._headerFirstPageHtml()).toBe('');
  });

  it('writes first-page header content to _headerFirstPageHtml when differentFirstPage is set', () => {
    component.headerContent = hf({
      html: 'DEFAULT',
      differentFirstPage: true,
      firstPageHtml: 'FIRST'
    });

    component.onHeaderInput(inputEvent('EDITED-FIRST'));

    expect((component as any)._headerFirstPageHtml()).toBe('EDITED-FIRST');
    expect((component as any)._headerHtml()).toBe('DEFAULT');
  });

  it('routes footer input the same way (first-page → _footerFirstPageHtml)', () => {
    component.footerContent = hf({
      html: 'DEFAULT',
      differentFirstPage: true,
      firstPageHtml: 'FIRST'
    });

    component.onFooterInput(inputEvent('EDITED-FIRST'));

    expect((component as any)._footerFirstPageHtml()).toBe('EDITED-FIRST');
    expect((component as any)._footerHtml()).toBe('DEFAULT');
  });

  it('emits the FULL header object (incl. firstPageHtml/evenHtml) on every change', () => {
    component.headerContent = hf({
      html: 'DEFAULT',
      differentFirstPage: true, firstPageHtml: 'FIRST',
      differentOddEven: true, evenHtml: 'EVEN'
    });
    const seen: HeaderFooterContent[] = [];
    component.headerChange.subscribe(v => seen.push(v));

    component.onHeaderInput(inputEvent('EDITED-FIRST'));

    expect(seen.length).toBe(1);
    expect(seen[0].firstPageHtml).toBe('EDITED-FIRST');
    expect(seen[0].html).toBe('DEFAULT');
    expect(seen[0].differentFirstPage).toBe(true);
    expect(seen[0].differentOddEven).toBe(true);
    expect(seen[0].evenHtml).toBe('EVEN');
  });

  it('odd page uses canonical _headerHtml (default = odd in OOXML)', () => {
    component.headerContent = hf({
      html: 'ODD-DEFAULT',
      differentOddEven: true, evenHtml: 'EVEN'
    });

    const odd = (component as any)._computeHeaderContent(0);
    const even = (component as any)._computeHeaderContent(1);

    expect(odd).toBe('ODD-DEFAULT');
    expect(even).toBe('EVEN');
  });

  it('an empty document (no header/footer) does not crash on input', () => {
    component.headerContent = hf({ html: '' });

    expect(() => component.onHeaderInput(inputEvent(''))).not.toThrow();
    expect((component as any)._headerHtml()).toBe('');
  });

  it('stopEditingHeaderFooter returns the editor to body mode', () => {
    (component as any).editingSection.set('header');

    component.stopEditingHeaderFooter();

    expect(component.editingSection()).toBe('body');
  });
});

describe('WysiwygEditorComponent — getContent nie materializuje auto-paginacji', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [WysiwygEditorComponent] }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;
  });

  function mockPages(...htmls: string[]) {
    const refs = htmls.map(h => {
      const el = document.createElement('div');
      el.innerHTML = h;
      return { nativeElement: el };
    });
    (component as any).pageEditorRefs = { toArray: () => refs };
  }

  it('łączy wiele stron BEZ wstawiania div.page-break', () => {
    mockPages('<p>Strona jeden</p>', '<p>Strona dwa</p>', '<p>Strona trzy</p>');

    const html = component.getContent();

    expect(html).toContain('Strona jeden');
    expect(html).toContain('Strona dwa');
    expect(html).toContain('Strona trzy');
    expect(html).not.toContain('page-break');
  });

  it('zachowuje jawny page-break użytkownika obecny w treści strony', () => {
    mockPages('<p>A</p><div class="page-break"></div><p>B</p>');

    const html = component.getContent();

    expect(html).toContain('class="page-break"');
  });

  it('scala fragmenty tej samej logicznej tabeli (R-17) zachowując kolumny', () => {
    mockPages(
      '<table data-split-table-id="st-1"><colgroup><col style="width:100px;"/></colgroup><tbody><tr><td>A1</td></tr></tbody></table>',
      '<table data-split-table-id="st-1"><colgroup><col style="width:100px;"/></colgroup><tbody><tr><td>A2</td></tr></tbody></table>'
    );

    const html = component.getContent();
    const tmp = document.createElement('div');
    tmp.innerHTML = html;

    expect(tmp.querySelectorAll('table').length).toBe(1);
    expect(tmp.querySelectorAll('tr').length).toBe(2);
    expect(html).toContain('colgroup');
    expect(html).not.toContain('data-split-table-id');
  });

  it('NIE scala niezależnych sąsiednich tabel (bez wspólnego id)', () => {
    mockPages('<table><tbody><tr><td>X</td></tr></tbody></table><table><tbody><tr><td>Y</td></tr></tbody></table>');

    const html = component.getContent();
    const tmp = document.createElement('div');
    tmp.innerHTML = html;

    expect(tmp.querySelectorAll('table').length).toBe(2);
  });

  it('_splitHtmlIntoPages zachowuje marker page-break (repaginate honoruje podział, zapis go nie gubi)', () => {
    const pages = (component as any)._splitHtmlIntoPages('<p>Przed</p><div class="page-break"></div><p>Po</p>');

    expect(pages.length).toBe(2);
    expect(pages[0]).toContain('class="page-break"');
    expect(pages[1]).toContain('Po');

    mockPages(...pages);
    expect(component.getContent()).toContain('class="page-break"');
  });

  it('marker sekcji DOCX (docx-section-break) przeżywa split na strony i zapis z pełnymi data-*', () => {
    const sectionMarker =
      '<div class="docx-section-break" data-break-type="nextPage"' +
      ' data-page-width-cm="29.7" data-page-height-cm="21" data-orientation="landscape"' +
      ' data-margin-top-cm="1.27"></div>';
    const html = `<p>Pion</p><div class="page-break"></div>${sectionMarker}<p>Poziom</p>`;

    const pages = (component as any)._splitHtmlIntoPages(html);

    expect(pages.length).toBe(2);
    expect(pages[1]).toContain('docx-section-break');

    mockPages(...pages);
    const saved = component.getContent();
    expect(saved).toContain('class="docx-section-break"');
    expect(saved).toContain('data-orientation="landscape"');
    expect(saved).toContain('data-page-width-cm="29.7"');
    expect(saved).toContain('data-break-type="nextPage"');
    expect(saved).toContain('class="page-break"');
  });

  it('_isPageBreakBlock wykrywa manualny page break (top-level i zagnieżdżony), nie zwykły akapit', () => {
    const top = document.createElement('div');
    top.className = 'page-break';
    expect((component as any)._isPageBreakBlock(top)).toBe(true);

    const nested = document.createElement('p');
    nested.innerHTML = '<span><div class="page-break"></div></span>';
    expect((component as any)._isPageBreakBlock(nested)).toBe(true);

    const plain = document.createElement('p');
    plain.textContent = 'zwykły tekst';
    expect((component as any)._isPageBreakBlock(plain)).toBe(false);
  });

  it('_captureDocumentDefaults czyta font-size/family z wrappera .document-content', () => {
    const html = '<div class="document-content" style="font-family:\'Times New Roman\';font-size:14pt;">'
      + '<p>Treść 14pt</p></div>';

    (component as any)._captureDocumentDefaults(html);

    expect(component.documentDefaultFontSize()).toBe('14pt');
    expect(component.documentDefaultFontFamily()).toContain('Times New Roman');
  });

  it('_captureDocumentDefaults bez wrappera nie ustawia defaultów (null = CSS edytora)', () => {
    (component as any)._captureDocumentDefaults('<p>Goła treść</p>');

    expect(component.documentDefaultFontSize()).toBeNull();
    expect(component.documentDefaultFontFamily()).toBeNull();
  });

  it('_captureDocumentDefaults bez wrappera RESETUJE defaulty poprzedniego dokumentu', () => {
    (component as any)._captureDocumentDefaults(
      '<div class="document-content" data-default-before-tw="120" data-default-after-tw="160"'
      + ' data-default-line="278" data-default-line-rule="auto" style="font-size:12pt;line-height:1.414;">'
      + '<p>Stary dokument</p></div>');
    expect(component.documentDefaultParagraphSpacing()).toBe('8pt');

    (component as any)._captureDocumentDefaults('<p>Nowy dokument bez wrappera</p>');

    expect(component.documentDefaultFontSize()).toBeNull();
    expect(component.documentDefaultLineHeight()).toBeNull();
    expect(component.documentDefaultParagraphSpacing()).toBeNull();
    expect(component.documentDefaultParagraphSpacingBefore()).toBeNull();
    expect(component.documentDefaultLineTw()).toBeNull();
  });

  it('_captureDocumentDefaults czyta data-para-spacing-sum i resetuje je dla kolejnego dokumentu', () => {
    (component as any)._captureDocumentDefaults(
      '<div class="document-content" data-para-spacing-sum="1" data-default-after-tw="0"><p>Suma</p></div>');
    expect(component.documentParagraphSpacingSum()).toBe(true);

    (component as any)._captureDocumentDefaults(
      '<div class="document-content" data-default-after-tw="0"><p>Max</p></div>');
    expect(component.documentParagraphSpacingSum()).toBe(false);

    (component as any)._captureDocumentDefaults(
      '<div class="document-content" data-para-spacing-sum="1"><p>Suma</p></div>');
    (component as any)._captureDocumentDefaults('<p>Nowy dokument bez wrappera</p>');
    expect(component.documentParagraphSpacingSum()).toBe(false);
  });

  it('_captureDocumentDefaults czyta interlinię i odstęp akapitu (data-default-after-tw)', () => {
    const html = '<div class="document-content" data-default-after-tw="160" data-default-line="278"'
      + ' data-default-line-rule="auto" style="font-size:12pt;line-height:1.158;">'
      + '<p>Treść</p></div>';

    (component as any)._captureDocumentDefaults(html);

    expect(component.documentDefaultLineHeight()).toBe('1.158');
    expect(component.documentDefaultParagraphSpacing()).toBe('8pt');
  });

  it('_captureDocumentDefaults honoruje jawne zera data-default-before/after-tw', () => {
    const html = '<div class="document-content" data-default-before-tw="0" data-default-after-tw="0">'
      + '<p>Ciasny dokument</p></div>';

    (component as any)._captureDocumentDefaults(html);

    expect(component.documentDefaultParagraphSpacing()).toBe('0pt');
    expect(component.documentDefaultParagraphSpacingBefore()).toBe('0pt');
  });

  it('_captureDocumentDefaults czyta odstęp przed akapitem (data-default-before-tw)', () => {
    const html = '<div class="document-content" data-default-before-tw="120" data-default-after-tw="160">'
      + '<p>Treść</p></div>';

    (component as any)._captureDocumentDefaults(html);

    expect(component.documentDefaultParagraphSpacingBefore()).toBe('6pt');
  });

  it('getContent odtwarza wrapper .document-content z pełnymi atrybutami', () => {
    const wrapped = '<div class="document-content" data-default-after-tw="160" data-default-line="278"'
      + ' data-default-line-rule="auto" style="font-family:\'Calibri\',sans-serif;font-size:12pt;line-height:1.158;">'
      + '<p>Strona 1</p></div>';

    const inner = (component as any)._captureDocumentDefaults(wrapped);
    expect(inner).toBe('<p>Strona 1</p>');

    mockPages('<p>Strona 1</p>', '<p>Strona 2</p>');
    const saved = component.getContent();

    const tmp = document.createElement('div');
    tmp.innerHTML = saved;
    const container = tmp.querySelector('.document-content') as HTMLElement;
    expect(container).not.toBeNull();
    expect(container.getAttribute('data-default-after-tw')).toBe('160');
    expect(container.getAttribute('data-default-line')).toBe('278');
    expect(container.style.fontSize).toBe('12pt');
    expect(container.style.lineHeight).toBe('1.158');
    expect(tmp.querySelectorAll('.document-content').length).toBe(1);
    expect(container.innerHTML).toContain('Strona 1');
    expect(container.innerHTML).toContain('Strona 2');
  });

  it('getContent bez przechwyconego wrappera zwraca treść bez zmian (zero regresji)', () => {
    mockPages('<p>A</p>');
    expect(component.getContent()).not.toContain('document-content');
  });
});

describe('WysiwygEditorComponent — geometria stron per sekcja', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [WysiwygEditorComponent] }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;
  });

  const A5 = { widthCm: 14.8, heightCm: 21, orientation: 'portrait' as const };

  it('baseGeometry: brak pageSize → A4 portrait; pageSize (A5) nadpisuje wymiary', () => {
    expect(component.baseGeometry().widthCm).toBe(21);
    expect(component.baseGeometry().heightCm).toBe(29.7);

    component.pageSize = A5;
    expect(component.baseGeometry().widthCm).toBe(14.8);
    expect(component.baseGeometry().heightCm).toBe(21);
    expect(component.pageWidthPx(0)).toBeCloseTo(14.8 * 37.8, 3);
  });

  it('baseGeometry: orientacja z inputu wygrywa — portrait-owe wymiary są obracane', () => {
    component.pageSize = A5;
    component.pageOrientation = 'landscape';

    const geo = component.baseGeometry();
    expect(geo.orientation).toBe('landscape');
    expect(geo.widthCm).toBe(21);
    expect(geo.heightCm).toBe(14.8);
  });

  it('marker otwierający stronę nadaje jej geometrię sekcji (landscape od strony 2)', () => {
    const html =
      '<p>Pion</p><div class="page-break"></div>' +
      '<div class="docx-section-break" data-break-type="nextPage"' +
      ' data-page-width-cm="29.7" data-page-height-cm="21" data-orientation="landscape"' +
      ' data-margin-top-cm="1.27" data-margin-left-cm="3"></div><p>Poziom</p>';

    component.content = html;

    const geos = component.pageGeometries();
    expect(geos.length).toBe(2);
    expect(geos[0].orientation).toBe('portrait');
    expect(component.isLandscapePage(0)).toBe(false);
    expect(geos[1].orientation).toBe('landscape');
    expect(geos[1].widthCm).toBeCloseTo(29.7, 3);
    expect(geos[1].margins.top).toBeCloseTo(1.27, 3);
    expect(geos[1].margins.left).toBeCloseTo(3, 3);
    expect(geos[1].margins.bottom).toBe(2.5);
    expect(component.pageWidthPx(1)).toBeCloseTo(29.7 * 37.8, 3);
  });

  it('marker w środku strony (continuous) zmienia geometrię dopiero od następnej strony', () => {
    const pages = [
      '<p>Sekcja 1</p><div class="docx-section-break" data-break-type="continuous"' +
        ' data-orientation="landscape" data-page-width-cm="29.7" data-page-height-cm="21"></div><p>Dalej</p>',
      '<p>Strona 2</p>',
    ];

    const geos = (component as any)._deriveGeometriesForPages(pages);

    expect(geos[0].orientation).toBe('portrait');
    expect(geos[1].orientation).toBe('landscape');
  });

  it('_parseSectionGeometry: brakujące/nieprawidłowe data-* dziedziczą z geometrii bieżącej', () => {
    const el = document.createElement('div');
    el.className = 'docx-section-break';
    el.setAttribute('data-page-width-cm', 'not-a-number');
    el.setAttribute('data-margin-top-cm', '0');

    const current = component.baseGeometry();
    const geo = (component as any)._parseSectionGeometry(el, current);

    expect(geo.widthCm).toBe(current.widthCm);
    expect(geo.margins.top).toBe(0);
    expect(geo.orientation).toBe(current.orientation);
  });

  it('_flattenTopBlocks nie rekursuje do wnętrza markera sekcji (marker przeżywa repaginację)', () => {
    const page = document.createElement('div');
    page.innerHTML = '<div class="docx-section-break" data-break-type="nextPage"></div>';

    const blocks = (component as any)._flattenTopBlocks(page);

    expect(blocks.length).toBe(1);
    expect(blocks[0].classList.contains('docx-section-break')).toBe(true);
  });
});

describe('WysiwygEditorComponent — dynamiczne pasmo nagłówka/stopki', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [WysiwygEditorComponent] }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;
  });

  it('baza: pasmo = height z headerContent, offset = margines − pasmo (odwrotność readera)', () => {
    component.headerContent = { html: '<p>H</p>', height: 1.25 };

    expect(component.headerBandPx(0)).toBeCloseTo(1.25 * 37.8, 3);
    expect(component.headerOffsetPx(0)).toBeCloseTo((2.5 - 1.25) * 37.8, 3);
  });

  it('sekcja z jawnym dystansem z markera: offset = dystans, pasmo = margines − dystans', () => {
    const html =
      '<p>Pion</p><div class="page-break"></div>' +
      '<div class="docx-section-break" data-break-type="nextPage"' +
      ' data-page-width-cm="29.7" data-page-height-cm="21" data-orientation="landscape"' +
      ' data-margin-top-cm="2" data-margin-bottom-cm="2"' +
      ' data-header-distance-cm="0.6" data-footer-distance-cm="0.5"></div><p>Poziom</p>';

    component.content = html;

    expect(component.headerOffsetPx(1)).toBeCloseTo(0.6 * 37.8, 3);
    expect(component.headerBandPx(1)).toBeCloseTo((2 - 0.6) * 37.8, 3);
    expect(component.footerOffsetPx(1)).toBeCloseTo(0.5 * 37.8, 3);
    expect(component.footerBandPx(1)).toBeCloseTo((2 - 0.5) * 37.8, 3);
  });

  it('offset + pasmo = margines górny (body zaczyna się na marginesie, gdy treść mieści się w paśmie)', () => {
    component.headerContent = { html: '<p>H</p>', height: 1.27 };

    const total = component.headerOffsetPx(0) + component.headerBandPx(0);
    expect(total).toBeCloseTo(2.5 * 37.8, 3);
  });
});

describe('WysiwygEditorComponent — nagłówki/stopki per sekcja', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;

  const twoSectionHtml =
    '<p>Sekcja 1</p><div class="page-break"></div>' +
    '<div class="docx-section-break" data-break-type="nextPage"' +
    ' data-page-width-cm="29.7" data-page-height-cm="21" data-orientation="landscape"></div>' +
    '<p>Sekcja 2</p>';

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [WysiwygEditorComponent] }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;
    component.headerContent = { html: 'BAZOWY', height: 1.27 };
    component.content = twoSectionHtml;
  });

  it('mapuje strony na sekcje (pageSectionIndexes) przy splicie treści', () => {
    expect(component.pageSectionIndexes()).toEqual([0, 1]);
  });

  it('strona sekcji z własnym nagłówkiem pokazuje nagłówek sekcji, wcześniejsze — bazowy', () => {
    component.sectionHeadersFooters = [
      { sectionIndex: 1, header: { html: 'SEKCYJNY', height: 1.27 } }
    ];

    expect((component as any)._computeHeaderContent(0)).toBe('BAZOWY');
    expect((component as any)._computeHeaderContent(1)).toBe('SEKCYJNY');
  });

  it('sekcja bez wpisu dziedziczy nagłówek z poprzedniego wpisu (jak Word)', () => {
    (component as any).pageSectionIndexes.set([0, 1, 2]);
    component.sectionHeadersFooters = [
      { sectionIndex: 1, header: { html: 'SEKCYJNY', height: 1.27 } }
    ];

    expect((component as any)._computeHeaderContent(2)).toBe('SEKCYJNY');
  });

  it('edycja nagłówka na stronie sekcji trafia do wpisu sekcji i emituje zmianę; baza nietknięta', () => {
    component.sectionHeadersFooters = [
      { sectionIndex: 1, header: { html: 'SEKCYJNY', height: 1.27 } }
    ];
    const emitted: unknown[] = [];
    component.sectionHeadersFootersChange.subscribe(v => emitted.push(v));

    component.editingHfPageIndex.set(1);
    component.onHeaderInput({ target: { innerHTML: 'EDYTOWANY-SEKCYJNY' } } as unknown as Event);

    expect(emitted.length).toBe(1);
    const entries = emitted[0] as { sectionIndex: number; header?: { html: string } }[];
    expect(entries[0].header!.html).toBe('EDYTOWANY-SEKCYJNY');
    expect((component as any)._headerHtml()).toBe('BAZOWY');
  });

  it('edycja nagłówka na stronie sekcji 0 edytuje bazę (bez dotykania wpisów sekcji)', () => {
    component.sectionHeadersFooters = [
      { sectionIndex: 1, header: { html: 'SEKCYJNY', height: 1.27 } }
    ];

    component.editingHfPageIndex.set(0);
    component.onHeaderInput({ target: { innerHTML: 'EDYTOWANA-BAZA' } } as unknown as Event);

    expect((component as any)._headerHtml()).toBe('EDYTOWANA-BAZA');
    expect((component as any)._sectionHF()[0].header.html).toBe('SEKCYJNY');
  });

  it('stopka sekcyjna: podmiana {page} działa też dla wpisu sekcji', () => {
    component.footerContent = { html: 'BAZOWA', height: 1.27 };
    component.sectionHeadersFooters = [
      { sectionIndex: 1, footer: { html: 'Strona {page}', height: 1.27 } }
    ];

    const footer = (component as any)._computeFooterContent(1);
    expect(footer).toBe('Strona 2');
  });

  it('nagłówek: pole PAGE/NUMPAGES jest podstawiane jak w stopce', () => {
    component.headerContent = {
      html: 'Strona <span class="field-page">{page}</span> z <span class="field-numpages">{pages}</span>',
      height: 1.27
    };

    const header = (component as any)._computeHeaderContent(0) as string;
    expect(header).toContain('Strona <span class="field-page">1</span>');
    expect(header).not.toContain('{page}');
    expect(header).not.toContain('{pages}');
  });

  it('stopka: tekst obok numeru strony (w tym formanty sdt-inline) trafia do treści strony', () => {
    component.footerContent = {
      html: '<p><span class="sdt-inline" data-sdt-tag="DocTitle">Nazwa dokumentu</span>'
        + 'Strona <span class="field-page">{page}</span> z <span class="field-numpages">{pages}</span>'
        + '<span class="sdt-inline" data-sdt-tag="Classification">Poufne</span></p>',
      height: 1.27
    };

    const footer = (component as any)._computeFooterContent(0) as string;
    expect(footer).toContain('Nazwa dokumentu');
    expect(footer).toContain('Poufne');
    expect(footer).toContain('Strona <span class="field-page">1</span>');
    expect(footer).not.toContain('{page}');
    expect(footer.indexOf('Nazwa dokumentu')).toBeLessThan(footer.indexOf('field-page'));
    expect(footer.indexOf('field-page')).toBeLessThan(footer.indexOf('Poufne'));
  });
});
