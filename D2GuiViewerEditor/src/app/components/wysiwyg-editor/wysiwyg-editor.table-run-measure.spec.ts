import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — tabela mierzona w przebiegu z sąsiadami (ADR-0108 r.8)', () => {
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

  it('akapit, tabela i akapit trafiają do JEDNEGO pomiaru przebiegu (kolaps marginesów jak na stronie)', () => {
    pageWith('<p>a</p><table><tbody><tr><td>t</td></tr></tbody></table><p>b</p>');
    const runs: string[][] = [];
    (component as any)._measureBlockRunHeights = (_m: HTMLElement, blocks: HTMLElement[]) => {
      runs.push(blocks.map(b => b.tagName));
      return blocks.map(() => 20);
    };
    (component as any)._blockMarginTopPx = () => 0;

    (component as any)._repaginateNow();

    expect(runs.some(r => r.length === 3 && r[1] === 'TABLE')).toBe(true);
    expect(component.pageContents().length).toBe(1);
  });

  it('tabela mieszcząca się w całości używa wysokości z przebiegu (bez osobnego pomiaru całej tabeli)', () => {
    pageWith('<p>a</p><table><tbody><tr><td>t</td></tr></tbody></table><p>b</p>');
    const singleTableMeasures: number[] = [];
    (component as any)._measureBlockRunHeights = (_m: HTMLElement, blocks: HTMLElement[]) => {
      if (blocks.length === 1 && blocks[0].tagName === 'TABLE') singleTableMeasures.push(1);
      return blocks.map(() => 20);
    };
    (component as any)._blockMarginTopPx = () => 0;

    (component as any)._repaginateNow();

    expect(singleTableMeasures.length).toBe(0);
  });
});
