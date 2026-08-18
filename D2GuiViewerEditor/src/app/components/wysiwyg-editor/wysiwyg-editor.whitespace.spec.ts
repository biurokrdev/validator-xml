import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — skompilowany CSS zachowuje wielokrotne spacje (pre-wrap)', () => {
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

  it('.editor-content ma white-space: pre-wrap', () => {
    const css = collectStyleText();
    expect(css).toMatch(/\.editor-content\s*\{[^}]*white-space:\s*pre-wrap/);
  });

  it('pasma nagłówka/stopki mają white-space: pre-wrap', () => {
    const css = collectStyleText();
    expect(css).toMatch(/\.header-editor-content[\s\S]{0,200}?white-space:\s*pre-wrap/);
    expect(css).toMatch(/\.footer-display[\s\S]{0,200}?white-space:\s*pre-wrap/);
  });

  it('nośnik tabulatora (min-width:2em) wraca do white-space: normal !important', () => {
    const css = collectStyleText();
    expect(css).toMatch(
      /span\[style\*=['"]min-width:2em['"]\][^{]*\{[^}]*white-space:\s*normal\s*!important/
    );
  });
});
