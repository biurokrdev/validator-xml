import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';

/**
 * Regresja (2026-09-27): „toolbar wszędzie pokazuje Calibri".
 *
 * Listener `selectionchange` na `document` był zakładany raz, z flagą boolean na document,
 * i wiązał `this` PIERWSZEJ instancji edytora na zawsze. Po nawigacji w SPA (admin → edytor,
 * otwarcie innego dokumentu) nowa instancja nie dostawała żadnego selectionchange, więc
 * `updateFormattingState` nigdy nie liczyło formatowania spod karetki — toolbar tkwił
 * w fallbacku z pierwszego runu dokumentu (Calibri 28 + bold tytułu), gdziekolwiek kliknięto.
 *
 * Kontrakt: jedyny listener na document należy do BIEŻĄCEJ instancji, a zniszczenie
 * instancji go zdejmuje.
 */
describe('WysiwygEditorComponent — listener selectionchange należy do bieżącej instancji', () => {
  const fixtures: ComponentFixture<WysiwygEditorComponent>[] = [];

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [WysiwygEditorComponent] }).compileComponents();
    delete (document as any).__wysiwygSelListener;
  });

  afterEach(() => {
    fixtures.splice(0).forEach(f => f.destroy());
    delete (document as any).__wysiwygSelListener;
  });

  function createEditor(): { component: WysiwygEditorComponent; onSelectionChange: ReturnType<typeof vi.fn> } {
    const fixture = TestBed.createComponent(WysiwygEditorComponent);
    fixtures.push(fixture);
    const component = fixture.componentInstance;
    const onSelectionChange = vi.fn();
    (component as any).onSelectionChange = onSelectionChange;
    (component as any).setupEventListeners();
    return { component, onSelectionChange };
  }

  function fireSelectionChange(): void {
    document.dispatchEvent(new Event('selectionchange'));
  }

  it('pierwsza instancja dostaje selectionchange', () => {
    const a = createEditor();
    fireSelectionChange();
    expect(a.onSelectionChange).toHaveBeenCalledTimes(1);
  });

  it('po zniszczeniu pierwszej instancji nowa instancja dostaje selectionchange (nawigacja w SPA)', () => {
    const a = createEditor();
    fixtures.shift()!.destroy();

    const b = createEditor();
    fireSelectionChange();

    expect(b.onSelectionChange).toHaveBeenCalledTimes(1);
    expect(a.onSelectionChange).not.toHaveBeenCalled();
  });

  it('nowa instancja przejmuje listener nawet gdy poprzednia jeszcze żyje (kolejność destroy/init routera)', () => {
    const a = createEditor();
    const b = createEditor();
    fireSelectionChange();

    expect(b.onSelectionChange).toHaveBeenCalledTimes(1);
    expect(a.onSelectionChange).not.toHaveBeenCalled();
  });

  it('ponowne setupEventListeners tej samej instancji nie dubluje wywołań', () => {
    const a = createEditor();
    (a.component as any).setupEventListeners();
    fireSelectionChange();
    expect(a.onSelectionChange).toHaveBeenCalledTimes(1);
  });

  it('zniszczenie bieżącej instancji zdejmuje listener i czyści flagę na document', () => {
    const a = createEditor();
    fixtures.shift()!.destroy();

    expect((document as any).__wysiwygSelListener).toBeUndefined();
    fireSelectionChange();
    expect(a.onSelectionChange).not.toHaveBeenCalled();
  });
});
