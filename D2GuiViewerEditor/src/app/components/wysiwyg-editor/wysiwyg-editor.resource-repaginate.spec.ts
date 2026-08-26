import { TestBed, ComponentFixture } from '@angular/core/testing';
import { vi } from 'vitest';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — korekcyjna repaginacja po załadowaniu zasobów', () => {
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
    vi.useRealTimers();
  });

  function setPageEditor(html: string): HTMLDivElement {
    const editor = document.createElement('div');
    editor.innerHTML = html;
    document.body.appendChild(editor);
    bodyEditors.push(editor);
    (component as any).pageEditorRefs = { toArray: () => [{ nativeElement: editor }] };
    return editor;
  }

  const flushMicrotasks = async () => {
    await new Promise(resolve => setTimeout(resolve, 0));
    await new Promise(resolve => setTimeout(resolve, 0));
  };

  it('przebieg korekcyjny jest odroczony do MAKROTASKU (po change detection), nie do mikrotasku', async () => {
    setPageEditor('<p>Treść</p>');
    const flush = vi.fn();
    (component as any)._flushPaginateNow = flush;

    (component as any)._repaginateAfterResources();
    for (let i = 0; i < 10; i++) await Promise.resolve();
    expect(flush).not.toHaveBeenCalled();

    await flushMicrotasks();
    expect(flush).toHaveBeenCalledTimes(1);
  });

  it('po ustabilizowaniu zasobów wykonuje DOKŁADNIE jeden przebieg repaginacji', async () => {
    setPageEditor('<p>Treść</p>');
    const flush = vi.fn();
    (component as any)._flushPaginateNow = flush;

    (component as any)._repaginateAfterResources();
    expect(flush).not.toHaveBeenCalled();

    await flushMicrotasks();
    expect(flush).toHaveBeenCalledTimes(1);
  });

  it('nowszy import anuluje oczekiwanie starszego (token generacji) — jeden przebieg', async () => {
    setPageEditor('<p>Treść</p>');
    const flush = vi.fn();
    (component as any)._flushPaginateNow = flush;

    (component as any)._repaginateAfterResources();
    (component as any)._repaginateAfterResources();

    await flushMicrotasks();
    expect(flush).toHaveBeenCalledTimes(1);
  });

  it('zniszczony komponent nie repaginuje po doładowaniu zasobów', async () => {
    setPageEditor('<p>Treść</p>');
    const flush = vi.fn();
    (component as any)._flushPaginateNow = flush;

    (component as any)._repaginateAfterResources();
    (component as any)._isDestroyed = true;

    await flushMicrotasks();
    expect(flush).not.toHaveBeenCalled();
  });

  it('_pendingImagesSettled czeka na obraz jeszcze-ładujący się i kończy na load', async () => {
    const editor = setPageEditor('<p><img alt="x"></p>');
    const img = editor.querySelector('img') as HTMLImageElement;
    Object.defineProperty(img, 'complete', { value: false, configurable: true });

    let settled = false;
    (component as any)._pendingImagesSettled().then(() => (settled = true));

    await flushMicrotasks();
    expect(settled).toBe(false);

    img.dispatchEvent(new Event('load'));
    await flushMicrotasks();
    expect(settled).toBe(true);
  });

  it('_pendingImagesSettled rozwiązuje się od razu, gdy brak ładujących się obrazów', async () => {
    setPageEditor('<p>Bez obrazów</p>');
    let settled = false;
    (component as any)._pendingImagesSettled().then(() => (settled = true));

    await flushMicrotasks();
    expect(settled).toBe(true);
  });
});
