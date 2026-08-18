import { describe, expect, it } from 'vitest';
import {
  applyColumnWidths,
  buildTableGrid,
  columnBoundaryForCell,
  readColumnWidths,
  syncTableColgroup,
  writeColgroupWidths,
} from './table-grid.util';

function makeTable(html: string): HTMLTableElement {
  const host = document.createElement('div');
  host.innerHTML = html;
  return host.querySelector('table') as HTMLTableElement;
}

function widthPx(el: Element): number {
  return Math.round(parseFloat((el as HTMLElement).style.width) || 0);
}

describe('syncTableColgroup', () => {
  function makeTable(html: string): HTMLTableElement {
    const host = document.createElement('div');
    host.innerHTML = html;
    return host.querySelector('table') as HTMLTableElement;
  }

  it('dokłada brakujące <col> po wstawieniu kolumny', () => {
    const table = makeTable(
      '<table><colgroup><col style="width:100px;"><col style="width:200px;"></colgroup>' +
        '<tr><td>a</td><td>b</td><td>NOWA</td></tr><tr><td>c</td><td>d</td><td>e</td></tr></table>'
    );
    syncTableColgroup(table);
    const cols = table.querySelectorAll('colgroup > col');
    expect(cols.length).toBe(3);
    expect((cols[0] as HTMLElement).style.width).toBe('100px');
    expect((cols[1] as HTMLElement).style.width).toBe('200px');
  });

  it('usuwa nadmiarowe <col> po usunięciu kolumny', () => {
    const table = makeTable(
      '<table><colgroup><col><col><col></colgroup>' +
        '<tr><td>a</td><td>b</td></tr></table>'
    );
    syncTableColgroup(table);
    expect(table.querySelectorAll('colgroup > col').length).toBe(2);
  });

  it('tworzy colgroup, gdy tabela go nie miała', () => {
    const table = makeTable('<table><tr><td>a</td><td>b</td></tr></table>');
    syncTableColgroup(table);
    const colgroup = table.querySelector(':scope > colgroup');
    expect(colgroup).not.toBeNull();
    expect(colgroup!.children.length).toBe(2);
    expect(table.firstElementChild).toBe(colgroup);
  });

  it('liczy kolumny siatki z uwzględnieniem colspan', () => {
    const table = makeTable(
      '<table><tr><td colspan="2">ab</td><td>c</td></tr><tr><td>a</td><td>b</td><td>c</td></tr></table>'
    );
    syncTableColgroup(table);
    expect(table.querySelectorAll('colgroup > col').length).toBe(3);
  });

  it('mocno scalona tabela (bez wiersza 1:1) zachowuje istniejące szerokości', () => {
    const table = makeTable(
      '<table><colgroup><col style="width:80px;"><col style="width:80px;"></colgroup>' +
        '<tr><td colspan="2">ab</td></tr></table>'
    );
    syncTableColgroup(table);
    const cols = table.querySelectorAll('colgroup > col');
    expect(cols.length).toBe(2);
    expect((cols[0] as HTMLElement).style.width).toBe('80px');
  });

  function stubCellWidths(table: HTMLTableElement, widths: number[]): void {
    Array.from(table.rows[0].cells).forEach((cell, i) => {
      Object.defineProperty(cell, 'getBoundingClientRect', {
        value: () => ({ width: widths[i] ?? 0 }) as DOMRect,
      });
    });
  }

  it('zmiana szerokości kolumny usuwa dokładne twips importu (data-w-tw)', () => {
    const table = makeTable(
      '<table><colgroup><col style="width:100px;" data-w-tw="1500"><col style="width:200px;" data-w-tw="3000"></colgroup>' +
        '<tr><td>a</td><td>b</td></tr></table>'
    );
    stubCellWidths(table, [150, 200]);
    syncTableColgroup(table);
    const cols = table.querySelectorAll('colgroup > col');
    expect((cols[0] as HTMLElement).style.width).toBe('150px');
    expect((cols[0] as HTMLElement).hasAttribute('data-w-tw')).toBe(false);
    expect((cols[1] as HTMLElement).style.width).toBe('200px');
    expect((cols[1] as HTMLElement).getAttribute('data-w-tw')).toBe('3000');
  });
});

describe('buildTableGrid', () => {
  it('mapuje prostą siatkę 2x2 na kolumny logiczne', () => {
    const grid = buildTableGrid(
      makeTable('<table><tr><td>a</td><td>b</td></tr><tr><td>c</td><td>d</td></tr></table>')
    );
    expect(grid.columnCount).toBe(2);
    expect(grid.cells.map(c => c.colStart)).toEqual([0, 1, 0, 1]);
  });

  it('colspan przesuwa colStart następnej komórki (nie td.cellIndex)', () => {
    const grid = buildTableGrid(
      makeTable(
        '<table><tr><td colspan="2">ab</td><td>c</td></tr><tr><td>a</td><td>b</td><td>c</td></tr></table>'
      )
    );
    expect(grid.columnCount).toBe(3);
    expect(grid.cells[1].colStart).toBe(2);
  });

  it('rowspan pozostawia lukę — komórka niżej dostaje właściwą kolumnę', () => {
    const grid = buildTableGrid(
      makeTable(
        '<table><tr><td rowspan="2">A</td><td>B</td></tr><tr><td>C</td></tr></table>'
      )
    );
    expect(grid.columnCount).toBe(2);
    const cellC = grid.cells.find(c => c.el.textContent === 'C')!;
    expect(cellC.colStart).toBe(1);
  });

  it('rozpoznaje dystansowe komórki gridBefore/gridAfter', () => {
    const grid = buildTableGrid(
      makeTable(
        '<table><tr><td data-grid-spacer="before"></td><td>x</td></tr></table>'
      )
    );
    expect(grid.cells[0].isSpacer).toBe(true);
    expect(grid.cells[1].isSpacer).toBe(false);
    expect(grid.cells[1].colStart).toBe(1);
  });
});

describe('columnBoundaryForCell', () => {
  it('prawa krawędź komórki colspan=2 to granica ostatniej zajmowanej kolumny', () => {
    const table = makeTable('<table><tr><td colspan="2">ab</td><td>c</td></tr></table>');
    const grid = buildTableGrid(table);
    const merged = table.querySelector('td')!;
    expect(columnBoundaryForCell(grid, merged, 'right')).toBe(1);
  });

  it('lewa krawędź pierwszej kolumny nie ma granicy (null)', () => {
    const table = makeTable('<table><tr><td>a</td><td>b</td></tr></table>');
    const grid = buildTableGrid(table);
    const first = table.querySelector('td')!;
    expect(columnBoundaryForCell(grid, first, 'left')).toBeNull();
  });

  it('lewa krawędź komórki po luce rowspan wskazuje kolumnę logiczną, nie cellIndex', () => {
    const table = makeTable(
      '<table><tr><td rowspan="2">A</td><td>B</td></tr><tr><td>C</td></tr></table>'
    );
    const grid = buildTableGrid(table);
    const cellC = Array.from(table.querySelectorAll('td')).find(td => td.textContent === 'C')!;
    expect(columnBoundaryForCell(grid, cellC, 'left')).toBe(0);
  });
});

describe('applyColumnWidths (spójność siatki po resize)', () => {
  it('komórka scalona dostaje sumę szerokości swoich kolumn, wiersze zostają wyrównane', () => {
    const table = makeTable(
      '<table><tr><td colspan="2">ab</td><td>c</td></tr><tr><td>a</td><td>b</td><td>c</td></tr></table>'
    );
    const grid = buildTableGrid(table);
    applyColumnWidths(table, grid, [100, 150, 200]);

    const row0 = table.rows[0].cells;
    const row1 = table.rows[1].cells;
    expect(widthPx(row0[0])).toBe(250); // colspan 0..1 = 100+150
    expect(widthPx(row0[1])).toBe(200); // kolumna 2
    expect(widthPx(row1[0])).toBe(100);
    expect(widthPx(row1[1])).toBe(150);
    expect(widthPx(row1[2])).toBe(200);
    expect(widthPx(table)).toBe(450);
  });

  it('regresja: resize granicy rusza tę samą kolumnę logiczną w każdym wierszu (mimo luki rowspan)', () => {
    const table = makeTable(
      '<table><tr><td rowspan="2">A</td><td>B</td></tr><tr><td>C</td></tr></table>'
    );
    const grid = buildTableGrid(table);
    applyColumnWidths(table, grid, [140, 160]);

    const cellA = Array.from(table.querySelectorAll('td')).find(td => td.textContent === 'A')!;
    const cellB = Array.from(table.querySelectorAll('td')).find(td => td.textContent === 'B')!;
    const cellC = Array.from(table.querySelectorAll('td')).find(td => td.textContent === 'C')!;
    expect(widthPx(cellA)).toBe(140); // kolumna 0
    expect(widthPx(cellB)).toBe(160); // kolumna 1
    expect(widthPx(cellC)).toBe(160); // kolumna 1 — wyrównana do B, nie do A
  });

  it('nie zeruje komórek nad kolumnami o nieznanej szerokości', () => {
    const table = makeTable('<table><tr><td>a</td><td>b</td></tr></table>');
    const grid = buildTableGrid(table);
    applyColumnWidths(table, grid, [0, 120]);
    const cells = table.rows[0].cells;
    expect(cells[0].style.width).toBe(''); // kolumna 0 nieznana → nietknięta
    expect(widthPx(cells[1])).toBe(120);
  });
});

describe('readColumnWidths', () => {
  it('czyta szerokości kolumn z colgroup (px)', () => {
    const table = makeTable(
      '<table><colgroup><col style="width:120px;"><col style="width:80px;"></colgroup>' +
        '<tr><td>a</td><td>b</td></tr></table>'
    );
    expect(readColumnWidths(table, buildTableGrid(table))).toEqual([120, 80]);
  });
});

describe('writeColgroupWidths', () => {
  it('zmieniona kolumna traci data-w-tw, niezmieniona zachowuje dokładne twips', () => {
    const table = makeTable(
      '<table><colgroup><col style="width:100px;" data-w-tw="1500"><col style="width:200px;" data-w-tw="3000"></colgroup>' +
        '<tr><td>a</td><td>b</td></tr></table>'
    );
    const grid = buildTableGrid(table);
    writeColgroupWidths(table, grid, [100, 200], [150, 200]);
    const cols = table.querySelectorAll('colgroup > col');
    expect(widthPx(cols[0])).toBe(150);
    expect((cols[0] as HTMLElement).hasAttribute('data-w-tw')).toBe(false);
    expect((cols[1] as HTMLElement).getAttribute('data-w-tw')).toBe('3000');
  });

  it('zapisuje colgroup z modelu także dla w pełni scalonej tabeli (brak czystego wiersza)', () => {
    const table = makeTable('<table><tr><td colspan="2">ab</td></tr></table>');
    const grid = buildTableGrid(table);
    writeColgroupWidths(table, grid, [80, 80], [120, 80]);
    const cols = table.querySelectorAll('colgroup > col');
    expect(cols.length).toBe(2);
    expect(widthPx(cols[0])).toBe(120);
  });

  it('ręczny resize zdejmuje markery data-tbl-w / data-tbl-layout (użytkownik przejmuje geometrię)', () => {
    const table = makeTable(
      '<table data-tbl-w="auto" data-tbl-layout="autofit" data-tbl-w-tw="9638">' +
        '<colgroup><col style="width:100px;"><col style="width:200px;"></colgroup>' +
        '<tr><td>a</td><td>b</td></tr></table>'
    );
    const grid = buildTableGrid(table);
    writeColgroupWidths(table, grid, [100, 200], [150, 200]);
    expect(table.hasAttribute('data-tbl-w')).toBe(false);
    expect(table.hasAttribute('data-tbl-layout')).toBe(false);
    expect(table.hasAttribute('data-tbl-w-tw')).toBe(false);
  });

  it('brak zmiany szerokości zachowuje markery semantyki szerokości', () => {
    const table = makeTable(
      '<table data-tbl-w="auto" data-tbl-layout="autofit">' +
        '<colgroup><col style="width:100px;"><col style="width:200px;"></colgroup>' +
        '<tr><td>a</td><td>b</td></tr></table>'
    );
    const grid = buildTableGrid(table);
    writeColgroupWidths(table, grid, [100, 200], [100, 200]);
    expect(table.getAttribute('data-tbl-w')).toBe('auto');
    expect(table.getAttribute('data-tbl-layout')).toBe('autofit');
  });

  it('brak zmiany szerokości zachowuje marker data-tbl-w-tw (oryginalne twipsy w:tblW)', () => {
    const table = makeTable(
      '<table data-tbl-w-tw="9638">' +
        '<colgroup><col style="width:100px;"><col style="width:200px;"></colgroup>' +
        '<tr><td>a</td><td>b</td></tr></table>'
    );
    const grid = buildTableGrid(table);
    writeColgroupWidths(table, grid, [100, 200], [100, 200]);
    expect(table.getAttribute('data-tbl-w-tw')).toBe('9638');
  });
});

describe('applyColumnWidths — markery semantyki szerokości', () => {
  it('resize całej tabeli zdejmuje data-tbl-w / data-tbl-layout / data-tbl-w-tw', () => {
    const table = makeTable(
      '<table data-tbl-w="auto" data-tbl-layout="autofit" data-tbl-w-tw="9638">' +
        '<tr><td>a</td><td>b</td></tr></table>'
    );
    const grid = buildTableGrid(table);
    applyColumnWidths(table, grid, [120, 180]);
    expect(table.style.width).toBe('300px');
    expect(table.hasAttribute('data-tbl-w')).toBe(false);
    expect(table.hasAttribute('data-tbl-layout')).toBe(false);
    expect(table.hasAttribute('data-tbl-w-tw')).toBe(false);
  });
});
