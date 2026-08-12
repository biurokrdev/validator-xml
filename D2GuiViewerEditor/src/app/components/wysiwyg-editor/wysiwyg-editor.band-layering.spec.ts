import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — warstwy pasm nagłówka/stopki vs treść (model Worda)', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [WysiwygEditorComponent] }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    fixture.detectChanges();
  });

  function collectStyleText(): string {
    return Array.from(document.querySelectorAll('style'))
      .map((s) => s.textContent ?? '')
      .join('\n');
  }

  function zIndexOf(pattern: RegExp, css: string): number | null {
    const match = pattern.exec(css);
    return match ? parseInt(match[1], 10) : null;
  }

  const bandPattern = /\.page-header,\s*\.page-footer\s*\{[^}]*?z-index:\s*(-?\d+)/;
  const editingPattern = /\.page-header\.editing,\s*\.page-footer\.editing\s*\{[^}]*?z-index:\s*(-?\d+)/;
  const contentPattern = /\.editor-content\s*\{[^}]*?z-index:\s*(-?\d+)/;

  it('kompiluje style komponentu do dokumentu (asercja ma na czym pracować)', () => {
    expect(collectStyleText()).toMatch(/\.page-header/);
  });

  it('pasmo nagłówka/stopki leży POD warstwą główną dokumentu', () => {
    const css = collectStyleText();
    const band = zIndexOf(bandPattern, css);
    const content = zIndexOf(contentPattern, css);

    expect(band).not.toBeNull();
    expect(content).not.toBeNull();
    expect(band!).toBeLessThan(content!);
  });

  it('pasmo w trybie edycji wychodzi PONAD warstwę główną', () => {
    const css = collectStyleText();
    const editing = zIndexOf(editingPattern, css);
    const content = zIndexOf(contentPattern, css);

    expect(editing).not.toBeNull();
    expect(content).not.toBeNull();
    expect(editing!).toBeGreaterThan(content!);
  });

  it('editor-content pozostaje stacking contextem (position:relative z z-index)', () => {
    const css = collectStyleText();

    expect(css).toMatch(/\.editor-content[^{]*\{[^}]*position:\s*relative/);
  });

  it('warstwa główna nad pasmem ma przezroczyste tło (treść pasma widoczna pod tekstem body)', () => {
    const css = collectStyleText();
    const raisedContentBlock = /\.editor-content\s*\{[^}]*?z-index:[^}]*?\}/.exec(css)?.[0] ?? '';

    expect(raisedContentBlock).toMatch(/background:\s*transparent/);
  });
});
