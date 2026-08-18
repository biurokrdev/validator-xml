import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — skompilowany CSS wyszarza nieaktywne pasma/body', () => {
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

  it('bezczynne pasma nagłówka/stopki są przygaszone (opacity, nie color)', () => {
    const css = collectStyleText();
    expect(css).toMatch(
      /\.page-header:not\(\.editing\)\s+\.header-display[^{}]*\{[^}]*opacity:\s*0?\.5/
    );
    expect(css).toMatch(
      /\.page-footer:not\(\.editing\)\s+\.footer-display[^{}]*\{[^}]*opacity:\s*0?\.5/
    );
  });

  it('podczas edycji pasma (band-editing) body i regiony przypisów są przygaszone', () => {
    const css = collectStyleText();
    expect(css).toMatch(
      /\.wysiwyg-editor-wrapper\.band-editing\s+:is\(\s*\.editor-content,\s*\.footnotes-region,\s*\.endnotes-region\s*\)\s*\{[^}]*opacity:\s*0?\.5/
    );
  });

  it('wrapper dostaje klasę band-editing, gdy edytowana jest sekcja inna niż body', () => {
    const component = fixture.componentInstance;
    const wrapper = fixture.nativeElement.querySelector('.wysiwyg-editor-wrapper') as HTMLElement;
    expect(wrapper.classList.contains('band-editing')).toBe(false);

    component.editingSection.set('header');
    fixture.detectChanges();
    expect(wrapper.classList.contains('band-editing')).toBe(true);

    component.editingSection.set('body');
    fixture.detectChanges();
    expect(wrapper.classList.contains('band-editing')).toBe(false);
  });
});
