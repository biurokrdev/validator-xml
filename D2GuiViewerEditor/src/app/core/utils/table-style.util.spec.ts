import {
  applyBorderScope,
  applyBorderToCells,
  classifyBorderTarget,
  refreshBorderlessMarker,
  restoreDefaultTableBorders,
  BORDERLESS_CELL_CLASS,
  DEFAULT_CELL_BORDER_COLOR
} from './table-style.util';
import { DEFAULT_TABLE_BORDER, TableBorderSettings } from '../../models/table-style.model';

function buildTable(rows: number, cols: number): HTMLTableElement {
  const table = document.createElement('table');
  for (let r = 0; r < rows; r++) {
    const tr = table.insertRow();
    for (let c = 0; c < cols; c++) {
      const td = tr.insertCell();
      td.textContent = `r${r}c${c}`;
      td.style.cssText = 'padding:8px;min-width:30px;';
    }
  }
  return table;
}

const border = DEFAULT_TABLE_BORDER;
function cellAt(t: HTMLTableElement, r: number, c: number): HTMLTableCellElement {
  return t.rows[r].cells[c];
}

describe('table-style.util — borders', () => {
  it('applyBorderScope("none") clears borders and keeps content', () => {
    const table = buildTable(2, 2);
    applyBorderScope(table, 'none', border);
    Array.from(table.querySelectorAll('td')).forEach(c =>
      expect((c as HTMLElement).style.borderStyle).toBe('none')
    );
    expect(cellAt(table, 0, 0).textContent).toBe('r0c0');
  });

  it('applyBorderScope("all") sets a full border on every cell', () => {
    const table = buildTable(2, 2);
    applyBorderScope(table, 'all', { color: '#000000', width: 2, style: 'solid' });
    const c = cellAt(table, 0, 0);
    expect(c.style.borderStyle).toBe('solid');
    expect(c.style.borderTopWidth).toBe('2px');
  });

  it('applyBorderScope("outer") only draws the table perimeter', () => {
    const table = buildTable(3, 3);
    applyBorderScope(table, 'outer', border);
    const topLeft = cellAt(table, 0, 0);
    expect(topLeft.style.borderTopStyle).toBe('solid');
    expect(topLeft.style.borderLeftStyle).toBe('solid');
    const center = cellAt(table, 1, 1);
    expect(center.style.borderTopStyle).toBe('');
    expect(center.style.borderLeftStyle).toBe('');
  });

  it('applyBorderScope("inner-horizontal") draws bottom lines except last row', () => {
    const table = buildTable(3, 2);
    applyBorderScope(table, 'inner-horizontal', border);
    expect(cellAt(table, 0, 0).style.borderBottomStyle).toBe('solid');
    expect(cellAt(table, 1, 0).style.borderBottomStyle).toBe('solid');
    expect(cellAt(table, 2, 0).style.borderBottomStyle).toBe('');
    expect(cellAt(table, 0, 0).style.borderRightStyle).toBe('');
  });

  it('applyBorderScope("inner-vertical") draws right lines except last column', () => {
    const table = buildTable(2, 3);
    applyBorderScope(table, 'inner-vertical', border);
    expect(cellAt(table, 0, 0).style.borderRightStyle).toBe('solid');
    expect(cellAt(table, 0, 1).style.borderRightStyle).toBe('solid');
    expect(cellAt(table, 0, 2).style.borderRightStyle).toBe('');
  });

  it('single-edge scopes touch only that edge of the table', () => {
    const table = buildTable(2, 2);
    applyBorderScope(table, 'top', border);
    expect(cellAt(table, 0, 0).style.borderTopStyle).toBe('solid');
    expect(cellAt(table, 1, 0).style.borderTopStyle).toBe('');

    const t2 = buildTable(2, 2);
    applyBorderScope(t2, 'right', border);
    expect(cellAt(t2, 0, 1).style.borderRightStyle).toBe('solid');
    expect(cellAt(t2, 0, 0).style.borderRightStyle).toBe('');
  });

  it('encodes the chosen line style and width into the border value', () => {
    const table = buildTable(1, 1);
    const dashed: TableBorderSettings = { color: '#ff0000', width: 3, style: 'dashed' };
    applyBorderScope(table, 'all', dashed);
    const c = cellAt(table, 0, 0);
    expect(c.style.borderStyle).toBe('dashed');
    expect(c.style.borderTopWidth).toBe('3px');
  });
});

describe('table-style.util — target cells (cell / selection)', () => {
  it('applyBorderToCells on a single cell affects only that cell', () => {
    const table = buildTable(2, 2);
    applyBorderToCells([cellAt(table, 0, 0)], 'all', { color: '#000', width: 2, style: 'solid' });
    expect(cellAt(table, 0, 0).style.borderStyle).toBe('solid');
    expect(cellAt(table, 1, 1).style.borderStyle).toBe('');
  });

  it('"outer" on a selected sub-range draws the range perimeter, not the table', () => {
    const table = buildTable(3, 3);
    const sel = [cellAt(table, 0, 0), cellAt(table, 0, 1), cellAt(table, 1, 0), cellAt(table, 1, 1)];
    applyBorderToCells(sel, 'outer', border);
    expect(cellAt(table, 0, 0).style.borderTopStyle).toBe('solid');
    expect(cellAt(table, 0, 0).style.borderLeftStyle).toBe('solid');
    expect(cellAt(table, 1, 1).style.borderBottomStyle).toBe('solid');
    expect(cellAt(table, 1, 1).style.borderRightStyle).toBe('solid');
    expect(cellAt(table, 2, 2).style.borderTopStyle).toBe('');
  });

  it('applyBorderToCells with empty selection is a no-op', () => {
    const table = buildTable(2, 2);
    expect(() => applyBorderToCells([], 'all', border)).not.toThrow();
    expect(cellAt(table, 0, 0).style.borderStyle).toBe('');
  });
});

describe('table-style.util — classifyBorderTarget (auto-scope)', () => {
  it('single cell → cell', () => {
    const t = buildTable(3, 3);
    expect(classifyBorderTarget(t, [cellAt(t, 1, 1)]).kind).toBe('cell');
  });

  it('every cell → table', () => {
    const t = buildTable(2, 2);
    const all = Array.from(t.querySelectorAll('td')) as HTMLTableCellElement[];
    expect(classifyBorderTarget(t, all).kind).toBe('table');
  });

  it('a full-width single row → row', () => {
    const t = buildTable(3, 3);
    const row = Array.from(t.rows[1].cells);
    const info = classifyBorderTarget(t, row);
    expect(info.kind).toBe('row');
    expect(info.rows).toBe(1);
  });

  it('a full-height single column → column', () => {
    const t = buildTable(3, 3);
    const col = [cellAt(t, 0, 2), cellAt(t, 1, 2), cellAt(t, 2, 2)];
    const info = classifyBorderTarget(t, col);
    expect(info.kind).toBe('column');
    expect(info.cols).toBe(1);
  });

  it('a partial block → range with its dimensions', () => {
    const t = buildTable(3, 3);
    const block = [cellAt(t, 0, 0), cellAt(t, 0, 1), cellAt(t, 1, 0), cellAt(t, 1, 1)];
    const info = classifyBorderTarget(t, block);
    expect(info.kind).toBe('range');
    expect(info).toEqual({ kind: 'range', rows: 2, cols: 2 });
  });

  it('empty set → none', () => {
    const t = buildTable(2, 2);
    expect(classifyBorderTarget(t, []).kind).toBe('none');
  });
});

describe('table-style.util — restore default', () => {
  it('restoreDefaultTableBorders sets the neutral 1px grid and keeps content', () => {
    const table = buildTable(2, 2);
    applyBorderScope(table, 'none', border);
    restoreDefaultTableBorders(table);
    const c = cellAt(table, 0, 0);
    expect(c.style.borderStyle).toBe('solid');
    expect(c.style.borderTopWidth).toBe('1px');
    expect(c.style.borderTopColor.replace(/\s/g, '')).toMatch(/rgb\(204,204,204\)|#cccccc/i);
    expect(c.textContent).toBe('r0c0');
    expect(DEFAULT_CELL_BORDER_COLOR).toBe('#cccccc');
  });
});

describe('table-style.util — marker symulowanej siatki (docx-borderless-cell)', () => {

  it('applyBorderScope("none") oznacza wszystkie komórki markerem siatki', () => {
    const table = buildTable(2, 2);
    applyBorderScope(table, 'none', border);
    Array.from(table.querySelectorAll('td')).forEach(c =>
      expect(c.classList.contains(BORDERLESS_CELL_CLASS)).toBe(true)
    );
  });

  it('nadanie widocznej linii zdejmuje marker', () => {
    const table = buildTable(2, 2);
    applyBorderScope(table, 'none', border);
    applyBorderScope(table, 'all', { color: '#000000', width: 1, style: 'solid' });
    Array.from(table.querySelectorAll('td')).forEach(c =>
      expect(c.classList.contains(BORDERLESS_CELL_CLASS)).toBe(false)
    );
  });

  it('częściowe obramowanie (jedna widoczna krawędź) nie dostaje markera', () => {
    const table = buildTable(2, 2);
    applyBorderScope(table, 'none', border);
    applyBorderScope(table, 'bottom', { color: '#000000', width: 1, style: 'solid' });
    expect(cellAt(table, 1, 0).classList.contains(BORDERLESS_CELL_CLASS)).toBe(false);
    expect(cellAt(table, 0, 0).classList.contains(BORDERLESS_CELL_CLASS)).toBe(true);
  });

  it('komórka bez inline (domyślna linia z arkusza) nie jest oznaczana', () => {
    const cell = buildTable(1, 1).rows[0].cells[0];
    refreshBorderlessMarker(cell);
    expect(cell.classList.contains(BORDERLESS_CELL_CLASS)).toBe(false);
  });

  it('szerokość 0 i kolor transparent liczą się jako brak linii', () => {
    const cell = buildTable(1, 1).rows[0].cells[0];
    cell.style.border = '0px solid #000000';
    refreshBorderlessMarker(cell);
    expect(cell.classList.contains(BORDERLESS_CELL_CLASS)).toBe(true);

    cell.style.border = '1px solid transparent';
    refreshBorderlessMarker(cell);
    expect(cell.classList.contains(BORDERLESS_CELL_CLASS)).toBe(true);
  });

  it('restoreDefaultTableBorders zdejmuje marker (wraca pełna siatka)', () => {
    const table = buildTable(2, 2);
    applyBorderScope(table, 'none', border);
    restoreDefaultTableBorders(table);
    Array.from(table.querySelectorAll('td')).forEach(c =>
      expect(c.classList.contains(BORDERLESS_CELL_CLASS)).toBe(false)
    );
  });
});
