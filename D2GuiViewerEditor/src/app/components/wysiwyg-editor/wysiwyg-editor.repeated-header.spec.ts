import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — powtarzane wiersze nagłówka tabeli (ADR-0108 r.9)', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [WysiwygEditorComponent] }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;
    (component as any)._measureTableRowsHeight = (_t: HTMLTableElement, subset: HTMLTableRowElement[]) => subset.length * 10;
    (component as any)._splitRowAtBudget = () => null;
  });

  function table(html: string): HTMLTableElement {
    const tmp = document.createElement('div');
    tmp.innerHTML = html;
    return tmp.querySelector('table') as HTMLTableElement;
  }

  const measurer = () => document.createElement('div');

  it('nagłówek (prefiks od 1. wiersza) jest kopiowany na kolejny fragment, budżet strony pomniejszony', () => {
    const t = table('<table><tbody><tr data-tbl-header="1"><td>H</td></tr><tr><td>a</td></tr><tr><td>b</td></tr><tr><td>c</td></tr></tbody></table>');
    const frags = (component as any)._splitTableForPagination(t, 25, 30, measurer(), 10) as HTMLTableElement[];

    expect(frags.length).toBe(2);
    const rows1 = Array.from(frags[1].rows);
    expect(rows1.map(r => r.textContent)).toEqual(['H', 'b', 'c']);
    expect(rows1[0].getAttribute('data-repeated-header')).toBe('1');
    expect(rows1[0].getAttribute('contenteditable')).toBe('false');
    expect(rows1[1].hasAttribute('data-repeated-header')).toBe(false);
  });

  it('nagłówek wyższy niż budżet kontynuacji: fragmenty nadal po jednym wierszu (bez pętli)', () => {
    const t = table('<table><tbody><tr data-tbl-header="1"><td>H</td></tr><tr><td>a</td></tr><tr><td>b</td></tr></tbody></table>');
    const frags = (component as any)._splitTableForPagination(t, 15, 15, measurer(), 10) as HTMLTableElement[];
    expect(frags.length).toBeGreaterThanOrEqual(2);
    frags.slice(1).forEach(f => expect(f.rows[0].getAttribute('data-repeated-header')).toBe('1'));
  });

  it('izolowany wiersz nagłówka (HDR04: wiersz 2 bez wiersza 1) NIE jest powtarzany', () => {
    const t = table('<table><tbody><tr><td>x</td></tr><tr data-tbl-header="1"><td>H2</td></tr><tr><td>a</td></tr><tr><td>b</td></tr></tbody></table>');
    const frags = (component as any)._splitTableForPagination(t, 25, 30, measurer(), 10) as HTMLTableElement[];
    expect(frags.length).toBe(2);
    expect(Array.from(frags[1].rows).some(r => r.hasAttribute('data-repeated-header'))).toBe(false);
  });

  it('scalanie fragmentów (getContent) usuwa kopie nagłówka — model źródłowy ma 1 nagłówek', () => {
    const t = table('<table><tbody><tr data-tbl-header="1"><td>H</td></tr><tr><td>a</td></tr><tr><td>b</td></tr><tr><td>c</td></tr></tbody></table>');
    const frags = (component as any)._splitTableForPagination(t, 25, 30, measurer(), 10) as HTMLTableElement[];
    const html = frags.map(f => f.outerHTML).join('');

    const merged = (component as any)._mergeSplitTables(html) as string;
    const tmp = document.createElement('div');
    tmp.innerHTML = merged;
    expect(tmp.querySelectorAll('table').length).toBe(1);
    expect(Array.from(tmp.querySelectorAll('tr')).map(r => r.textContent)).toEqual(['H', 'a', 'b', 'c']);
    expect(merged).not.toContain('data-repeated-header');
  });
});
