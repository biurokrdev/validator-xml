
export interface TableGridCell {
  readonly el: HTMLTableCellElement;
  readonly rowIndex: number;
  readonly colStart: number;
  readonly colSpan: number;
  readonly rowSpan: number;
  readonly isSpacer: boolean;
}

export interface TableGrid {
  readonly columnCount: number;
  readonly cells: readonly TableGridCell[];
  readonly rows: readonly HTMLTableRowElement[];
}

export type ColumnEdge = 'left' | 'right';

function clampSpan(value: number): number {
  return Number.isFinite(value) && value > 1 ? Math.floor(value) : 1;
}

export function buildTableGrid(table: HTMLTableElement): TableGrid {
  const rows = Array.from(table.rows);
  const cells: TableGridCell[] = [];
  const occupancy: number[] = [];
  let columnCount = 0;

  for (let rowIndex = 0; rowIndex < rows.length; rowIndex++) {
    let col = 0;
    for (const el of Array.from(rows[rowIndex].cells)) {
      while ((occupancy[col] ?? 0) > 0) col++;

      const colSpan = clampSpan(el.colSpan);
      const rowSpan = clampSpan(el.rowSpan);
      cells.push({
        el,
        rowIndex,
        colStart: col,
        colSpan,
        rowSpan,
        isSpacer: el.hasAttribute('data-grid-spacer'),
      });

      for (let c = col; c < col + colSpan; c++) occupancy[c] = rowSpan;
      col += colSpan;
    }

    columnCount = Math.max(columnCount, col, occupancy.length);
    for (let c = 0; c < occupancy.length; c++) {
      if (occupancy[c] > 0) occupancy[c]--;
    }
  }

  return { columnCount, cells, rows };
}

export function findGridCell(grid: TableGrid, el: HTMLTableCellElement): TableGridCell | null {
  return grid.cells.find(c => c.el === el) ?? null;
}

export function columnBoundaryForCell(
  grid: TableGrid,
  el: HTMLTableCellElement,
  edge: ColumnEdge
): number | null {
  const cell = findGridCell(grid, el);
  if (!cell) return null;
  if (edge === 'right') return cell.colStart + cell.colSpan - 1;
  return cell.colStart > 0 ? cell.colStart - 1 : null;
}

function parsePx(value: string | null | undefined): number {
  if (!value) return 0;
  const match = /(-?\d+(?:\.\d+)?)px/.exec(value);
  return match ? Math.max(0, parseFloat(match[1])) : 0;
}

function colgroupCols(table: HTMLTableElement): HTMLElement[] {
  return Array.from(table.querySelectorAll(':scope > colgroup > col')) as HTMLElement[];
}

export function readColumnWidths(table: HTMLTableElement, grid: TableGrid): number[] {
  const widths = new Array<number>(grid.columnCount).fill(0);

  colgroupCols(table).forEach((col, i) => {
    if (i < widths.length) {
      const px = parsePx(col.style.width);
      if (px > 0) widths[i] = px;
    }
  });

  for (const cell of grid.cells) {
    if (cell.colSpan !== 1 || cell.isSpacer || widths[cell.colStart] > 0) continue;
    const measured = Math.round(cell.el.getBoundingClientRect().width);
    if (measured > 0) widths[cell.colStart] = measured;
  }

  return widths;
}

export function applyColumnWidths(table: HTMLTableElement, grid: TableGrid, widths: number[]): void {
  for (const cell of grid.cells) {
    let sum = 0;
    let known = true;
    for (let c = cell.colStart; c < cell.colStart + cell.colSpan; c++) {
      const w = widths[c] ?? 0;
      if (w > 0) sum += w;
      else {
        known = false;
        break;
      }
    }
    if (known && sum > 0) cell.el.style.width = `${Math.round(sum)}px`;
  }

  const total = widths.reduce((s, w) => s + (w > 0 ? w : 0), 0);
  if (total > 0) {
    table.style.width = `${Math.round(total)}px`;
    dropRenderOnlyWidthMarkers(table);
  }
}

function dropRenderOnlyWidthMarkers(table: HTMLTableElement): void {
  table.removeAttribute('data-tbl-w');
  table.removeAttribute('data-tbl-layout');
  table.removeAttribute('data-tbl-w-tw');
}

function ensureColgroup(table: HTMLTableElement, columnCount: number): HTMLElement {
  let colgroup = table.querySelector(':scope > colgroup') as HTMLElement | null;
  if (!colgroup) {
    colgroup = document.createElement('colgroup');
    table.insertBefore(colgroup, table.firstChild);
  }
  while (colgroup.children.length > columnCount) colgroup.removeChild(colgroup.lastChild!);
  while (colgroup.children.length < columnCount) colgroup.appendChild(document.createElement('col'));
  return colgroup;
}

export function writeColgroupWidths(
  table: HTMLTableElement,
  grid: TableGrid,
  startWidths: number[],
  newWidths: number[]
): void {
  const cols = Array.from(ensureColgroup(table, grid.columnCount).children) as HTMLElement[];
  let anyChanged = false;
  for (let i = 0; i < grid.columnCount; i++) {
    const col = cols[i];
    if (!col) continue;
    const nw = Math.round(newWidths[i] ?? 0);
    if (nw <= 0) continue;
    const changed = Math.round(startWidths[i] ?? 0) !== nw;
    if (changed) {
      col.style.width = `${nw}px`;
      col.removeAttribute('data-w-tw');
      anyChanged = true;
    } else if (parsePx(col.style.width) <= 0) {
      col.style.width = `${nw}px`;
    }
  }
  if (anyChanged) dropRenderOnlyWidthMarkers(table);
}

export function syncTableColgroup(table: HTMLTableElement): void {
  const grid = buildTableGrid(table);
  if (grid.columnCount <= 0) return;

  ensureColgroup(table, grid.columnCount);
  const cols = colgroupCols(table);

  const byRow = new Map<number, TableGridCell[]>();
  for (const cell of grid.cells) {
    const bucket = byRow.get(cell.rowIndex);
    if (bucket) bucket.push(cell);
    else byRow.set(cell.rowIndex, [cell]);
  }
  let reference: TableGridCell[] | null = null;
  for (const rowCells of byRow.values()) {
    if (
      rowCells.length === grid.columnCount &&
      rowCells.every(c => c.colSpan === 1 && !c.isSpacer)
    ) {
      reference = rowCells.slice().sort((a, b) => a.colStart - b.colStart);
      break;
    }
  }
  if (!reference) return;

  reference.forEach(cell => {
    const measured = Math.round(cell.el.getBoundingClientRect().width);
    if (measured <= 0) return;
    const col = cols[cell.colStart];
    if (!col) return;
    const newWidth = `${measured}px`;
    if (col.style.width !== newWidth) {
      col.style.width = newWidth;
      col.removeAttribute('data-w-tw');
    }
  });
}
