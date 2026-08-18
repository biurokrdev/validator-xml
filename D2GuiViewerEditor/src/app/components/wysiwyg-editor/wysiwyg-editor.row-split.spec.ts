import { TestBed, ComponentFixture } from '@angular/core/testing';
import { WysiwygEditorComponent } from './wysiwyg-editor';

describe('WysiwygEditorComponent — dzielenie wiersza tabeli między strony', () => {
  let fixture: ComponentFixture<WysiwygEditorComponent>;
  let component: WysiwygEditorComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [WysiwygEditorComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(WysiwygEditorComponent);
    component = fixture.componentInstance;

    const anyC = component as unknown as Record<string, unknown>;
    anyC['_measureTableRowsHeight'] = (_t: HTMLTableElement, subset: HTMLTableRowElement[]) =>
      subset.reduce((sum, tr) => sum + tr.querySelectorAll('li, p').length * 100, 0);
    anyC['_measureRowLayout'] = (_t: HTMLTableElement, row: HTMLTableRowElement) =>
      Array.from(row.cells).map(cell => {
        const blocks = Array.from(cell.children) as HTMLElement[];
        const tops: number[] = [];
        const bottoms: number[] = [];
        let y = 0;
        for (const b of blocks) {
          const h = (b.tagName === 'OL' || b.tagName === 'UL')
            ? b.querySelectorAll('li').length * 100
            : 100;
          tops.push(y);
          bottoms.push(y + h);
          y += h;
        }
        return { contentWidthPx: 500, blockTops: tops, blockBottoms: bottoms };
      });
    anyC['_measureListItemBottoms'] = (list: HTMLElement) =>
      Array.from(list.children).map((_li, i) => (i + 1) * 100);
  });

  function splitTable(
    table: HTMLTableElement, firstAvail: number, fullAvail: number
  ): HTMLTableElement[] {
    const measurer = document.createElement('div');
    return (component as unknown as {
      _splitTableForPagination(
        t: HTMLTableElement, f: number, a: number, m: HTMLElement, lh: number
      ): HTMLTableElement[];
    })._splitTableForPagination(table, firstAvail, fullAvail, measurer, 20);
  }

  function tableWithBigListRow(extraRowAttrs = ''): HTMLTableElement {
    const host = document.createElement('div');
    host.innerHTML =
      `<table data-no-borders="1"><colgroup><col style="width:500px"></colgroup><tbody>` +
      `<tr data-row-height-tw="13032" style="height:868px"${extraRowAttrs}>` +
      `<td style="vertical-align:top;">` +
      `<ol><li>P1</li><li>P2</li><li>P3</li><li>P4</li><li>P5</li><li>P6</li></ol>` +
      `</td></tr></tbody></table>`;
    return host.querySelector('table') as HTMLTableElement;
  }

  it('wiersz wyższy niż strona jest cięty WEWNĄTRZ: część listy zostaje w resztce strony, reszta płynie dalej', () => {
    const fragments = splitTable(tableWithBigListRow(), 250, 350);

    expect(fragments.length).toBe(3);
    const id = fragments[0].getAttribute('data-split-table-id');
    expect(id).toBeTruthy();
    fragments.forEach(f => expect(f.getAttribute('data-split-table-id')).toBe(id));
    fragments.forEach(f => expect(f.querySelector('colgroup')).not.toBeNull());

    const trs = fragments.map(f => f.querySelector('tr') as HTMLTableRowElement);
    const rowId = trs[0].getAttribute('data-split-row-id');
    expect(rowId).toBeTruthy();
    trs.forEach(tr => expect(tr.getAttribute('data-split-row-id')).toBe(rowId));

    expect(trs[0].hasAttribute('data-split-row')).toBe(false);
    expect(trs[0].getAttribute('data-row-height-tw')).toBe('13032');
    expect(trs[1].getAttribute('data-split-row')).toBe('cont');
    expect(trs[2].getAttribute('data-split-row')).toBe('cont');
    expect(trs[1].hasAttribute('data-row-height-tw')).toBe(false);
    trs.forEach(tr => expect(tr.style.height).toBe(''));

    const liCounts = trs.map(tr => tr.querySelectorAll('li').length);
    expect(liCounts).toEqual([2, 3, 1]);
    expect(trs[1].querySelector('ol')?.getAttribute('start')).toBe('3');
    expect(trs[2].querySelector('ol')?.getAttribute('start')).toBe('6');
    const texts = trs.flatMap(tr => Array.from(tr.querySelectorAll('li')).map(li => li.textContent));
    expect(texts).toEqual(['P1', 'P2', 'P3', 'P4', 'P5', 'P6']);
  });

  it('w:cantSplit blokuje cięcie wiersza (wiersz atomowy jak dotąd)', () => {
    const fragments = splitTable(tableWithBigListRow(' data-cant-split="1"'), 250, 350);
    expect(fragments.length).toBe(1);
    expect(fragments[0].querySelector('tr[data-split-row-id]')).toBeNull();
    expect(fragments[0].querySelectorAll('li').length).toBe(6);
  });

  it('hRule=exact blokuje cięcie wiersza (Word przycina, nie łamie)', () => {
    const fragments = splitTable(tableWithBigListRow(' data-row-hrule="exact"'), 250, 350);
    expect(fragments.length).toBe(1);
    expect(fragments[0].querySelector('tr[data-split-row-id]')).toBeNull();
  });

  it('wiersz OBJĘTY scaleniem pionowym (rowspan) jest atomowy — bez cięcia wewnątrz', () => {
    const host = document.createElement('div');
    host.innerHTML =
      `<table><tbody>` +
      `<tr><td rowspan="2"><p>A</p></td>` +
      `<td><ol><li>1</li><li>2</li><li>3</li><li>4</li><li>5</li><li>6</li></ol></td></tr>` +
      `<tr><td><p>B</p></td></tr>` +
      `</tbody></table>`;
    const table = host.querySelector('table') as HTMLTableElement;

    const fragments = splitTable(table, 250, 350);
    expect(fragments.every(f => f.querySelector('tr[data-split-row-id]') === null)).toBe(true);
    const allLis = fragments.flatMap(f => Array.from(f.querySelectorAll('li')));
    expect(allLis.length).toBe(6);
    const liPerFragment = fragments.map(f => f.querySelectorAll('li').length);
    expect(liPerFragment.filter(n => n > 0)).toEqual([6]);
  });

  it('rowspan w INNYCH wierszach nie blokuje cięcia wiersza nieobjętego scaleniem (RC3)', () => {
    const host = document.createElement('div');
    host.innerHTML =
      `<table><tbody>` +
      `<tr><td rowspan="2"><p>A</p></td><td><p>B</p></td></tr>` +
      `<tr><td><p>C</p></td></tr>` +
      `<tr><td><p>X</p></td>` +
      `<td><ol><li>1</li><li>2</li><li>3</li><li>4</li><li>5</li><li>6</li></ol></td></tr>` +
      `</tbody></table>`;
    const table = host.querySelector('table') as HTMLTableElement;

    const fragments = splitTable(table, 350, 350);
    expect(fragments.length).toBe(3);
    expect(fragments[0].querySelectorAll('tr').length).toBe(2);
    expect(fragments[0].querySelector('td[rowspan]')).not.toBeNull();
    expect(fragments[0].querySelector('tr[data-split-row-id]')).toBeNull();
    const headTr = fragments[1].querySelector('tr') as HTMLTableRowElement;
    const contTr = fragments[2].querySelector('tr') as HTMLTableRowElement;
    expect(headTr.getAttribute('data-split-row-id')).toBeTruthy();
    expect(contTr.getAttribute('data-split-row')).toBe('cont');
    expect(fragments[1].querySelectorAll('li').length).toBe(3);
    expect(fragments[2].querySelectorAll('li').length).toBe(3);
    const texts = [headTr, contTr].flatMap(tr =>
      Array.from(tr.querySelectorAll('li')).map(li => li.textContent));
    expect(texts).toEqual(['1', '2', '3', '4', '5', '6']);
  });

  it('tabela zagnieżdżona w komórce nie wnosi swoich wierszy do chunkingu tabeli zewnętrznej (RC4)', () => {
    const host = document.createElement('div');
    host.innerHTML =
      `<table><tbody>` +
      `<tr><td><p>W1</p></td></tr>` +
      `<tr><td><table><tbody><tr><td><p>N</p></td></tr></tbody></table></td></tr>` +
      `<tr><td><p>W3</p></td></tr>` +
      `</tbody></table>`;
    const table = host.querySelector('table') as HTMLTableElement;

    const fragments = splitTable(table, 250, 250);
    expect(fragments.length).toBe(2);
    expect(fragments[0].querySelectorAll(':scope > tbody > tr').length).toBe(2);
    const nested = fragments[0].querySelector('td table') as HTMLTableElement;
    expect(nested).not.toBeNull();
    expect(nested.querySelectorAll('tr').length).toBe(1);
    expect(nested.textContent).toContain('N');
    expect(fragments[1].querySelectorAll('tr').length).toBe(1);
    expect(fragments[1].textContent).toContain('W3');
    const allText = fragments.map(f => f.textContent).join('');
    expect(allText.match(/N/g)?.length).toBe(1);
  });

  it('zapis scala fragmenty z powrotem w JEDEN wiersz (komórki sklejone, split-para scalone, markery zdjęte)', () => {
    const html =
      `<table data-split-table-id="st-9"><tbody>` +
      `<tr data-split-row-id="sr-9" data-row-height-tw="13032"><td>` +
      `<p>Początek akapitu</p></td></tr>` +
      `</tbody></table>` +
      `<table data-split-table-id="st-9"><tbody>` +
      `<tr data-split-row-id="sr-9" data-split-row="cont"><td>` +
      `<p data-split-para="cont" style="margin-top:0;"> i jego dokończenie.</p><p>Drugi.</p></td></tr>` +
      `</tbody></table>`;

    const merged = (component as unknown as { _mergeSplitTables(h: string): string })
      ._mergeSplitTables(html);

    const tmp = document.createElement('div');
    tmp.innerHTML = merged;
    expect(tmp.querySelectorAll('table').length).toBe(1);
    const trs = tmp.querySelectorAll('tr');
    expect(trs.length).toBe(1);
    expect(trs[0].getAttribute('data-row-height-tw')).toBe('13032');
    expect(trs[0].hasAttribute('data-split-row-id')).toBe(false);
    expect(trs[0].hasAttribute('data-split-row')).toBe(false);
    expect(merged).not.toContain('data-split-table-id');
    const paras = trs[0].querySelectorAll('p');
    expect(paras.length).toBe(2);
    expect(paras[0].textContent).toBe('Początek akapitu i jego dokończenie.');
    expect(merged).not.toContain('data-split-para');
  });

  it('pre-merge (keepIds) + ponowne cięcie reuse\'uje ID — HTML stabilny między repaginacjami', () => {
    const fragments = splitTable(tableWithBigListRow(), 250, 350);
    const rowId = fragments[0].querySelector('tr')!.getAttribute('data-split-row-id');
    const tableId = fragments[0].getAttribute('data-split-table-id');

    const target = fragments[0];
    const targetBody = target.querySelector('tbody')!;
    for (let i = 1; i < fragments.length; i++) {
      fragments[i].querySelectorAll('tr').forEach(tr => targetBody.appendChild(tr));
    }
    (component as unknown as { _mergeSplitRowsIn(t: HTMLTableElement, k: boolean): void })
      ._mergeSplitRowsIn(target, true);

    expect(target.querySelectorAll('tr').length).toBe(1);
    const mergedTr = target.querySelector('tr')!;
    expect(mergedTr.getAttribute('data-split-row-id')).toBe(rowId);
    expect(mergedTr.querySelectorAll('ol').length).toBe(1);
    expect(mergedTr.querySelectorAll('li').length).toBe(6);

    const again = splitTable(target, 250, 350);
    expect(again.length).toBe(3);
    expect(again[0].getAttribute('data-split-table-id')).toBe(tableId);
    again.forEach(f =>
      expect(f.querySelector('tr')!.getAttribute('data-split-row-id')).toBe(rowId));
  });

  it('regresja: tabela wielowierszowa nadal dzieli się MIĘDZY wierszami (bez cięcia wewnątrz)', () => {
    const host = document.createElement('div');
    host.innerHTML =
      `<table><tbody>` +
      `<tr><td><p>W1</p></td></tr><tr><td><p>W2</p></td></tr><tr><td><p>W3</p></td></tr>` +
      `</tbody></table>`;
    const table = host.querySelector('table') as HTMLTableElement;

    const fragments = splitTable(table, 250, 250);
    expect(fragments.length).toBe(2);
    expect(fragments[0].querySelectorAll('tr').length).toBe(2);
    expect(fragments[1].querySelectorAll('tr').length).toBe(1);
    expect(fragments.every(f => f.querySelector('tr[data-split-row-id]') === null)).toBe(true);
  });
});
