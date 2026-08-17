import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — cross-page caret + page-number font', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [WysiwygEditorComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;
  });

  function setupTwoPages(): { page0: HTMLDivElement; page1: HTMLDivElement } {
    const page0 = document.createElement('div');
    page0.textContent = 'Tekst pierwszej strony';
    const page1 = document.createElement('div');
    page1.textContent = 'Tekst drugiej strony';
    document.body.appendChild(page0);
    document.body.appendChild(page1);
    const refs = [{ nativeElement: page0 }, { nativeElement: page1 }];
    (component as any).pageEditorRefs = { toArray: () => refs };
    return { page0, page1 };
  }

  function collapseCaretIn(node: Node, offset: number): void {
    const range = document.createRange();
    range.setStart(node, offset);
    range.collapse(true);
    const sel = window.getSelection()!;
    sel.removeAllRanges();
    sel.addRange(range);
  }

  it('ArrowDown moves the caret from the bottom of a page to the next page', () => {
    const { page0 } = setupTwoPages();
    (component as any)._isCaretOnEdgeLine = () => true;
    collapseCaretIn(page0.firstChild!, 3);

    const moved = (component as any)._tryMoveCaretAcrossPages('down');

    expect(moved).toBe(true);
    expect(component.activePageIndex()).toBe(1);
  });

  it('ArrowUp moves the caret from the top of a page to the previous page', () => {
    const { page1 } = setupTwoPages();
    (component as any)._isCaretOnEdgeLine = () => true;
    component.activePageIndex.set(1);
    collapseCaretIn(page1.firstChild!, 2);

    const moved = (component as any)._tryMoveCaretAcrossPages('up');

    expect(moved).toBe(true);
    expect(component.activePageIndex()).toBe(0);
  });

  it('does not move when the selection is a range (Shift+Arrow keeps selecting)', () => {
    const { page0 } = setupTwoPages();
    const range = document.createRange();
    range.setStart(page0.firstChild!, 0);
    range.setEnd(page0.firstChild!, 5); 
    const sel = window.getSelection()!;
    sel.removeAllRanges();
    sel.addRange(range);

    const moved = (component as any)._tryMoveCaretAcrossPages('down');

    expect(moved).toBe(false);
    expect(component.activePageIndex()).toBe(0);
  });

  it('does not move past the last page on ArrowDown', () => {
    const { page1 } = setupTwoPages();
    component.activePageIndex.set(1);
    collapseCaretIn(page1.firstChild!, 3);

    const moved = (component as any)._tryMoveCaretAcrossPages('down');

    expect(moved).toBe(false);
  });

  it('does nothing with a single page', () => {
    const page0 = document.createElement('div');
    page0.textContent = 'Jedyna strona';
    document.body.appendChild(page0);
    (component as any).pageEditorRefs = { toArray: () => [{ nativeElement: page0 }] };
    collapseCaretIn(page0.firstChild!, 2);

    expect((component as any)._tryMoveCaretAcrossPages('down')).toBe(false);
  });

  function setupBreakPages(page1Text = 'B'): { page0: HTMLDivElement; page1: HTMLDivElement } {
    const page0 = document.createElement('div');
    page0.innerHTML = '<p>A</p><div class="page-break"></div>';
    const page1 = document.createElement('div');
    page1.innerHTML = `<p>${page1Text}</p>`;
    document.body.appendChild(page0);
    document.body.appendChild(page1);
    (component as any).pageEditorRefs = { toArray: () => [{ nativeElement: page0 }, { nativeElement: page1 }] };
    
    (component as any)._schedulePaginate = () => {};
    (component as any)._schedulePersist = () => {};
    (component as any).updateState = () => {};
    return { page0, page1 };
  }

  it('Backspace at page start removes the previous page trailing page-break', () => {
    const { page0, page1 } = setupBreakPages();
    collapseCaretIn(page1.querySelector('p')!.firstChild!, 0); 

    const removed = (component as any)._tryDeletePageBreakBackwards();

    expect(removed).toBe(true);
    expect(page0.querySelector('.page-break')).toBeNull();
  });

  it('Backspace mid-line does not remove the page-break (normal char delete)', () => {
    const { page0, page1 } = setupBreakPages('Bcd');
    collapseCaretIn(page1.querySelector('p')!.firstChild!, 1); 

    const removed = (component as any)._tryDeletePageBreakBackwards();

    expect(removed).toBe(false);
    expect(page0.querySelector('.page-break')).not.toBeNull();
  });

  it('does not delete a page break (or the page wrapper) on the first page', () => {
    const page0 = document.createElement('div');
    page0.innerHTML = '<p>Solo</p>';
    document.body.appendChild(page0);
    (component as any).pageEditorRefs = { toArray: () => [{ nativeElement: page0 }] };
    collapseCaretIn(page0.querySelector('p')!.firstChild!, 0);

    expect((component as any)._tryDeletePageBreakBackwards()).toBe(false);
  });

  function setupAutoPages(page0Html: string, page1Html: string): { page0: HTMLDivElement; page1: HTMLDivElement } {
    const page0 = document.createElement('div');
    page0.innerHTML = page0Html;
    const page1 = document.createElement('div');
    page1.innerHTML = page1Html;
    document.body.appendChild(page0);
    document.body.appendChild(page1);
    (component as any).pageEditorRefs = { toArray: () => [{ nativeElement: page0 }, { nativeElement: page1 }] };
    (component as any)._schedulePaginate = () => {};
    (component as any)._schedulePersist = () => {};
    (component as any).updateState = () => {};
    return { page0, page1 };
  }

  it('Backspace removes an empty leading paragraph on the auto-paginated 2nd page', () => {
    const { page0, page1 } = setupAutoPages('<p>Pierwsza</p>', '<p></p>');
    collapseCaretIn(page1.querySelector('p')!, 0); 

    const handled = (component as any)._tryMergeAcrossPageBackwards();

    expect(handled).toBe(true);
    expect(page1.querySelector('p')).toBeNull(); 
  });

  it('Backspace merges the first block of page 2 into the last block of page 1', () => {
    const { page0, page1 } = setupAutoPages('<p>Linia A</p>', '<p>Linia B</p>');
    collapseCaretIn(page1.querySelector('p')!.firstChild!, 0); 

    const handled = (component as any)._tryMergeAcrossPageBackwards();

    expect(handled).toBe(true);
    expect(page0.lastElementChild!.textContent).toBe('Linia ALinia B'); 
    expect(page1.querySelector('p')).toBeNull();
  });

  it('does not merge mid-line (only at the very start of the page)', () => {
    const { page1 } = setupAutoPages('<p>A</p>', '<p>Bcd</p>');
    collapseCaretIn(page1.querySelector('p')!.firstChild!, 1); 

    expect((component as any)._tryMergeAcrossPageBackwards()).toBe(false);
  });

  it('does not merge incompatible blocks (table) — navigates instead, content intact', () => {
    const { page0, page1 } = setupAutoPages('<table><tr><td>x</td></tr></table>', '<p>Treść</p>');
    collapseCaretIn(page1.querySelector('p')!.firstChild!, 0);

    const handled = (component as any)._tryMergeAcrossPageBackwards();

    expect(handled).toBe(true);
    expect(page0.querySelector('table')).not.toBeNull(); 
    expect(page1.querySelector('p')).not.toBeNull();     
    expect(component.activePageIndex()).toBe(0);
  });

  it('saveSelection NIE nadpisuje realnego zaznaczenia pustą karetką, gdy edytor stracił fokus', () => {
    const { page0 } = setupTwoPages();
    page0.setAttribute('contenteditable', 'true');
    page0.focus();

    const range = document.createRange();
    range.selectNodeContents(page0.firstChild!);
    const sel = window.getSelection()!;
    sel.removeAllRanges();
    sel.addRange(range);
    (component as any).saveSelection();
    const saved = (component as any).savedSelection?.toString();
    expect(saved).toContain('Tekst pierwszej strony');

    const input = document.createElement('input');
    document.body.appendChild(input);
    input.focus();
    collapseCaretIn(page0.firstChild!, 0);
    (component as any).saveSelection(); 

    expect((component as any).savedSelection?.toString()).toBe(saved);
  });

  it('page number inherits an inline font-size from the footer content', () => {
    const footer = document.createElement('div');
    footer.innerHTML = '<span style="font-size:8pt">Strona </span>';

    const html = (component as any)._pageNumberHtml(footer);

    expect(html).toContain('class="page-number"');
    expect(html).toContain('font-size:8pt');
    expect(html).toContain('{page}');
  });

  it('_inlineFieldFontSize reads the first inline-sized span', () => {
    const footer = document.createElement('div');
    footer.innerHTML = 'Strona <span style="font-size:9pt">x</span>';

    expect((component as any)._inlineFieldFontSize(footer)).toBe('9pt');
  });
});
