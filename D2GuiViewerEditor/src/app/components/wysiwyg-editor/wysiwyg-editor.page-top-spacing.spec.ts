import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — odstęp „przed" na szczycie strony (ADR-0108)', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;
  const bodyEditors: HTMLElement[] = [];

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [WysiwygEditorComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    bodyEditors.splice(0).forEach(el => el.remove());
  });

  function pageWith(html: string): HTMLDivElement {
    const editor = document.createElement('div');
    editor.innerHTML = html;
    document.body.appendChild(editor);
    bodyEditors.push(editor);
    (component as any).pageEditorRefs = { toArray: () => [{ nativeElement: editor }] };
    return editor;
  }

  function stubMeasurement(heightPerBlock: number): { marginCalls: string[] } {
    const marginCalls: string[] = [];
    (component as any)._measureBlockRunHeights = (_m: HTMLElement, blocks: HTMLElement[]) =>
      blocks.map(() => heightPerBlock);
    (component as any)._blockMarginTopPx = (block: HTMLElement) => {
      marginCalls.push(block.textContent ?? '');
      const m = /margin-top:\s*([\d.]+)/.exec(block.getAttribute('style') ?? '');
      return m ? parseFloat(m[1]) : 0;
    };
    return { marginCalls };
  }

  const H1 = '<h1 style="margin-top:24pt;">Rozdział</h1>';

  it('po twardym łamaniu strony pierwszy akapit dostaje .fmt-page-top (odstęp „przed" zdjęty)', () => {
    pageWith(`<p>a</p><div class="page-break"></div>${H1}<p>b</p>`);
    stubMeasurement(20);

    (component as any)._repaginateNow();

    const pages = component.pageContents();
    expect(pages.length).toBe(2);
    expect(pages[1]).toMatch(/<h1[^>]*class="fmt-page-top"[^>]*>Rozdział<\/h1>/);
    expect(pages[1]).toContain('margin-top:24pt');
    expect(pages[1]).toContain('<p>b</p>');
  });

  it('na pierwszej stronie dokumentu odstęp „przed" ZOSTAJE (jak w Wordzie)', () => {
    pageWith(`${H1}<p>a</p>`);
    stubMeasurement(20);

    (component as any)._repaginateNow();

    expect(component.pageContents()[0]).not.toContain('fmt-page-top');
  });

  it('na pierwszej stronie NOWEJ SEKCJI odstęp „przed" ZOSTAJE (marker docx-section-break)', () => {
    pageWith(
      `<p>a</p><div class="page-break"></div>` +
      `<div class="docx-section-break" data-break-type="nextPage"></div>${H1}<p>b</p>`
    );
    stubMeasurement(20);

    (component as any)._repaginateNow();

    const pages = component.pageContents();
    expect(pages.length).toBe(2);
    expect(pages[1]).toContain('docx-section-break');
    expect(pages[1]).not.toContain('fmt-page-top');
  });

  it('przy NATURALNYM przelaniu pierwszy blok nowej strony też traci odstęp „przed"', () => {
    pageWith(`<p>a</p><p>b</p><p style="margin-top:24pt;">c</p><p>d</p>`);
    stubMeasurement(400);

    (component as any)._repaginateNow();

    const pages = component.pageContents();
    expect(pages.length).toBeGreaterThanOrEqual(2);
    expect(pages[1]).toMatch(/<p[^>]*class="fmt-page-top"[^>]*>c<\/p>/);
  });

  it('pomiar bloku na szczycie strony jest pomniejszony o margin-top (budżet = render)', () => {
    pageWith(`<p>a</p><div class="page-break"></div>${H1}<p>b</p>`);
    const { marginCalls } = stubMeasurement(20);

    (component as any)._repaginateNow();

    expect(marginCalls).toEqual(['Rozdział']);
  });

  it('getContent() nie wynosi klasy .fmt-page-top do zapisu', () => {
    pageWith(`<p>a</p><div class="page-break"></div>${H1}<p>b</p>`);
    stubMeasurement(20);
    (component as any)._repaginateNow();
    expect(component.pageContents()[1]).toContain('fmt-page-top');

    const pages = component.pageContents();
    bodyEditors.splice(0).forEach(el => el.remove());
    const editors = pages.map(html => {
      const el = document.createElement('div');
      el.innerHTML = html;
      document.body.appendChild(el);
      bodyEditors.push(el);
      return { nativeElement: el };
    });
    (component as any).pageEditorRefs = { toArray: () => editors };

    const saved = component.getContent();

    expect(saved).not.toContain('fmt-page-top');
    expect(saved).toContain('margin-top:24pt');
  });

  describe('measurer mierzy w warunkach strony', () => {
    const fakeCs = {
      fontFamily: 'Calibri',
      fontSize: '14px',
      lineHeight: '19.656px',
    } as unknown as CSSStyleDeclaration;

    it('używa AUTORSKIEJ interlinii kontenera (mnożnik), nie wyliczonych px', () => {
      component.documentDefaultLineHeight.set('1.404');

      const m = (component as any)._createBlockMeasurer(fakeCs, 600) as HTMLElement;

      expect(m.style.lineHeight).toBe('1.404');
    });

    it('bez domyślnej interlinii dokumentu zostaje dotychczasowe px z computed style', () => {
      component.documentDefaultLineHeight.set(null);

      const m = (component as any)._createBlockMeasurer(fakeCs, 600) as HTMLElement;

      expect(m.style.lineHeight).toBe('19.656px');
    });

    it('kopiuje zmienne CSS strony (odstępy domyślne, jednostka linii, single fontu)', () => {
      component.documentDefaultParagraphSpacing.set('0pt');
      component.documentDefaultParagraphSpacingBefore.set('0pt');
      component.documentDefaultLineTw.set('276');

      const m = (component as any)._createBlockMeasurer(fakeCs, 600) as HTMLElement;
      const css = m.style.cssText;

      expect(css).toMatch(/--doc-par-margin:\s*0pt/);
      expect(css).toMatch(/--doc-par-margin-top:\s*0pt/);
      expect(css).toMatch(/--w-line-tw:\s*276/);
      expect(css).toMatch(/--w-line-single:\s*\S/);
    });

    it('brak wartości = brak zmiennej (fallback SCSS po obu stronach, spójnie)', () => {
      component.documentDefaultParagraphSpacing.set(null);
      component.documentDefaultParagraphSpacingBefore.set(null);
      component.documentDefaultLineTw.set(null);

      const m = (component as any)._createBlockMeasurer(fakeCs, 600) as HTMLElement;

      expect(m.style.cssText).not.toContain('--doc-par-margin');
      expect(m.style.cssText).not.toContain('--w-line-tw');
    });
  });

  it('klasa z poprzedniej paginacji jest zdejmowana przed pomiarem (układ liczy się od nowa)', () => {
    pageWith(`<h1 class="fmt-page-top" style="margin-top:24pt;">Rozdział</h1><p>a</p>`);
    stubMeasurement(20);

    (component as any)._repaginateNow();

    expect(component.pageContents()[0]).not.toContain('fmt-page-top');
  });
});
