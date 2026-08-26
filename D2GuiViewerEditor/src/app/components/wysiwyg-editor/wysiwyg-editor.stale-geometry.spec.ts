import { TestBed, ComponentFixture } from '@angular/core/testing';
import { vi } from 'vitest';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — nieaktualna geometria strony w DOM (ADR-0108 r.7)', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;
  const mounted: HTMLElement[] = [];

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [WysiwygEditorComponent] }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    mounted.splice(0).forEach(el => el.remove());
  });

  function pageWith(pageClientWidth: number, editorClientWidth: number): void {
    const page = document.createElement('div');
    page.className = 'page';
    const editor = document.createElement('div');
    editor.className = 'editor-content';
    editor.style.padding = '0px';
    editor.innerHTML = '<p>a</p><p>b</p>';
    page.appendChild(editor);
    document.body.appendChild(page);
    mounted.push(page);
    Object.defineProperty(page, 'clientWidth', { value: pageClientWidth, configurable: true });
    Object.defineProperty(editor, 'clientWidth', { value: editorClientWidth, configurable: true });
    (component as any).pageEditorRefs = { toArray: () => [{ nativeElement: editor }] };
    (component as any)._measureBlockRunHeights = (_m: HTMLElement, blocks: HTMLElement[]) => blocks.map(() => 20);
    (component as any)._blockMarginTopPx = () => 0;
  }

  function measurerWidths(): number[] {
    const widths: number[] = [];
    const orig = (component as any)._createBlockMeasurer.bind(component);
    (component as any)._createBlockMeasurer = (cs: CSSStyleDeclaration, w: number) => {
      widths.push(w);
      return orig(cs, w);
    };
    return widths;
  }

  const CSS_PX_PER_CM = 37.8;
  const geometryPageWidth = () => component.baseGeometry().widthCm * CSS_PX_PER_CM;
  const geometryContentWidth = () => {
    const g = component.baseGeometry();
    return (g.widthCm - g.margins.left - g.margins.right) * CSS_PX_PER_CM;
  };

  it('strona w DOM o INNEJ szerokości niż geometria → pomiar wg geometrii + jeden przebieg po renderze', () => {
    pageWith(geometryPageWidth() - 100, 500);
    const widths = measurerWidths();
    const soon = vi.fn();
    (component as any)._flushPaginateSoon = soon;

    (component as any)._repaginateNow();

    expect(widths[0]).toBeCloseTo(geometryContentWidth(), 0);
    expect(soon).toHaveBeenCalledTimes(1);

    (component as any)._repaginateNow();
    expect(soon).toHaveBeenCalledTimes(1);
  });

  it('strona w DOM zgodna z geometrią → skala z DOM jak dotąd, bez dodatkowego przebiegu', () => {
    pageWith(geometryPageWidth(), 500);
    const widths = measurerWidths();
    const soon = vi.fn();
    (component as any)._flushPaginateSoon = soon;

    (component as any)._repaginateNow();

    expect(widths[0]).toBeCloseTo(500, 0);
    expect(soon).not.toHaveBeenCalled();
  });
});
