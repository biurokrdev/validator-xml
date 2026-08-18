import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — „Pokaż wszystko": <br> zamykający akapit', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [WysiwygEditorComponent] }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  function trailingBrBlock(html: string): HTMLElement | null {
    const holder = document.createElement('div');
    holder.innerHTML = html;
    document.body.appendChild(holder);
    try {
      const brs = holder.querySelectorAll('br');
      const br = brs[brs.length - 1] as HTMLElement;
      return (component as any)._trailingBrBlock(br);
    } finally {
      holder.remove();
    }
  }

  it('wykrywa <br> zamykający akapit (także w spanie runu)', () => {
    expect(trailingBrBlock('<p><span>tekst<br></span></p>')?.tagName).toBe('P');
    expect(trailingBrBlock('<p><br></p>')?.tagName).toBe('P');
    expect(trailingBrBlock('<h2>tytuł<br></h2>')?.tagName).toBe('H2');
  });

  it('wykrywa <br> zamykający blockquote/pre (bloki ze ścieżki wklejania)', () => {
    expect(trailingBrBlock('<blockquote>cytat<br></blockquote>')?.tagName).toBe('BLOCKQUOTE');
    expect(trailingBrBlock('<pre>kod<br></pre>')?.tagName).toBe('PRE');
  });

  it('skompilowany CSS daje ¶ (::after) także dla blockquote/pre', () => {
    const css = Array.from(document.querySelectorAll('style'))
      .map(s => s.textContent ?? '')
      .join('\n');

    expect(css).toMatch(/:is\(p, h1, h2, h3, h4, h5, h6, li, blockquote, pre\)::after/);
  });

  it('NIE oznacza <br> w środku treści (tekst lub element za złamaniem)', () => {
    expect(trailingBrBlock('<p><span>a<br>b</span></p>')).toBeNull();
    expect(trailingBrBlock('<p><span>a<br></span><span>b</span></p>')).toBeNull();
    expect(trailingBrBlock('<p><span>a<br><img src="x.png"></span></p>')).toBeNull();
  });

  it('biały znak/ZWSP za <br> nie liczy się jako treść', () => {
    expect(trailingBrBlock('<p><span>a<br>\n \u200B</span></p>')?.tagName).toBe('P');
  });

  it('z dwóch <br> pod rząd tylko OSTATNI zamyka akapit', () => {
    const holder = document.createElement('div');
    holder.innerHTML = '<p><span>a<br><br></span></p>';
    document.body.appendChild(holder);
    try {
      const [first, last] = Array.from(holder.querySelectorAll('br'));
      expect((component as any)._trailingBrBlock(first)).toBeNull();
      expect((component as any)._trailingBrBlock(last)?.tagName).toBe('P');
    } finally {
      holder.remove();
    }
  });

  it('overlay nadaje klasę .fmt-trailing-br tylko blokom zamkniętym <br> i zdejmuje ją po wyłączeniu trybu', () => {
    const host: HTMLElement = (component as any)._hostRef.nativeElement;
    const page = document.createElement('div');
    page.className = 'page';
    page.innerHTML =
      '<div class="editor-content">' +
      '<p id="closed"><span>x<br></span></p>' +
      '<p id="open"><span>a<br>b</span></p>' +
      '</div>';
    host.appendChild(page);

    component.showFormattingMarks.set(true);
    (component as any)._renderFormattingMarksOverlay();

    expect(page.querySelector('#closed')!.classList.contains('fmt-trailing-br')).toBe(true);
    expect(page.querySelector('#open')!.classList.contains('fmt-trailing-br')).toBe(false);

    component.showFormattingMarks.set(false);
    (component as any)._renderFormattingMarksOverlay();

    expect(page.querySelector('.fmt-trailing-br')).toBeNull();
    expect(page.querySelector('#closed')!.getAttribute('class')).toBeNull();
  });

  it('getContent() nie przepuszcza klasy prezentacyjnej do zapisu', () => {
    const el = document.createElement('div');
    el.innerHTML =
      '<p class="fmt-trailing-br"><span>x<br></span></p>' +
      '<p class="moja-klasa fmt-trailing-br"><span>y<br></span></p>';
    (component as any).pageEditorRefs = { toArray: () => [{ nativeElement: el }] };

    const html = component.getContent();

    expect(html).not.toContain('fmt-trailing-br');
    expect(html).toContain('moja-klasa'); // inne klasy bloku przeżywają
    expect(html).not.toContain('class=""'); // bez pustych atrybutów po zdjęciu
  });

  it('skompilowany CSS gasi ::after (¶) dla bloków .fmt-trailing-br', () => {
    const css = Array.from(document.querySelectorAll('style'))
      .map(s => s.textContent ?? '')
      .join('\n');
    const block = /\.fmt-trailing-br::after\s*\{[^}]*\}/.exec(css)?.[0] ?? '';

    expect(block).toMatch(/content:\s*none/);
  });
});
