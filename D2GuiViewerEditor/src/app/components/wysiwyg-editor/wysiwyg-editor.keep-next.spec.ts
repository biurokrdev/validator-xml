import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — keepNext / keepLines w paginacji (ADR-0108 r.3)', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;
  const bodyEditors: HTMLElement[] = [];

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [WysiwygEditorComponent] }).compileComponents();
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

  function stub(heightPerBlock: number): void {
    (component as any)._measureBlockRunHeights = (_m: HTMLElement, blocks: HTMLElement[]) =>
      blocks.map(() => heightPerBlock);
    (component as any)._blockMarginTopPx = () => 0;
  }

  const texts = (html: string) =>
    Array.from(html.matchAll(/<p[^>]*>([^<]*)<\/p>/g)).map(m => m[1]);

  it('blok keep-next jedzie na nową stronę RAZEM z następnym, który się nie zmieścił', () => {
    pageWith('<p>a</p><p style="break-after:avoid;">b</p><p>c</p>');
    stub(400);

    (component as any)._repaginateNow();

    const pages = component.pageContents();
    expect(pages.length).toBe(2);
    expect(texts(pages[0])).toEqual(['a']);
    expect(texts(pages[1])).toEqual(['b', 'c']);
  });

  it('łańcuch keep-next przenosi się w całości (etykieta + próbka + następny)', () => {
    pageWith('<p>a</p><p>b</p><p style="break-after:avoid;">c</p><p style="break-after:avoid;">d</p><p>e</p>');
    stub(250);

    (component as any)._repaginateNow();

    const pages = component.pageContents();
    expect(texts(pages[0])).toEqual(['a', 'b']);
    expect(texts(pages[1])).toEqual(['c', 'd', 'e']);
  });

  it('łańcuch nie opróżnia strony — co najmniej jeden blok zostaje (jak Word)', () => {
    pageWith('<p style="break-after:avoid;">a</p><p style="break-after:avoid;">b</p><p>c</p>');
    stub(400);

    (component as any)._repaginateNow();

    const pages = component.pageContents();
    expect(texts(pages[0])).toEqual(['a']);
    expect(texts(pages[1])).toEqual(['b', 'c']);
  });

  it('gdy następny blok jest DZIELONY (1. linia zostaje), keep-next nie ciągnie poprzednika', () => {
    pageWith('<p>a</p><p style="break-after:avoid;">b</p><p>ccc</p>');
    stub(400);
    (component as any)._splitBlockAtBudget = (block: HTMLElement) => {
      if (block.textContent !== 'ccc') return null;
      const head = block.cloneNode(false) as HTMLElement; head.textContent = 'c1';
      const cont = block.cloneNode(false) as HTMLElement; cont.textContent = 'c2';
      cont.setAttribute('data-split-para', 'cont');
      return [head, cont];
    };

    (component as any)._repaginateNow();

    const pages = component.pageContents();
    expect(texts(pages[0])).toEqual(['a', 'b', 'c1']);
    expect(texts(pages[1])).toEqual(['c2']);
  });

  it('keepLines: akapit nie jest dzielony — w całości na kolejną stronę', () => {
    pageWith('<p>a</p><p>b</p><p style="break-inside:avoid;">long</p>');
    stub(400);
    const splitSpy = vi.fn(() => null);
    (component as any)._splitBlockAtBudget = splitSpy;

    (component as any)._repaginateNow();

    const pages = component.pageContents();
    expect(texts(pages[1])).toEqual(['long']);
    expect(splitSpy).not.toHaveBeenCalled();
  });

  it('jawne break-after:auto (val=false) nie tworzy łańcucha', () => {
    pageWith('<p>a</p><p style="break-after:auto;">b</p><p>c</p>');
    stub(400);

    (component as any)._repaginateNow();

    const pages = component.pageContents();
    expect(texts(pages[0])).toEqual(['a', 'b']);
    expect(texts(pages[1])).toEqual(['c']);
  });
});
