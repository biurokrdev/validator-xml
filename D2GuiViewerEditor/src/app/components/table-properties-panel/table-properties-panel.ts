import { Component, EventEmitter, Input, Output, signal, ChangeDetectionStrategy } from '@angular/core';
import {
  TableBorderLineStyle,
  TableBorderScope,
  TABLE_BORDER_COLORS,
  TABLE_BORDER_LINE_STYLES,
  TABLE_BORDER_SCOPES,
  TABLE_BORDER_WIDTHS
} from '../../models/table-style.model';
import { BorderTargetInfo } from '../../core/utils/table-style.util';

@Component({
  selector: 'd2-table-properties-panel',
  standalone: true,
  imports: [],
  templateUrl: './table-properties-panel.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './table-properties-panel.scss'
})
export class TablePropertiesPanelComponent {
  @Input() hasActiveTable = false;
  @Input() gridLinesVisible = true;
  @Input() shadingColors: string[] = [];

  @Input() borderColor = '#cccccc';
  @Input() borderWidth = 1;
  @Input() borderStyle: TableBorderLineStyle = 'solid';
  @Input() borderTargetInfo: BorderTargetInfo | null = null;
  @Input() lastBorderScope: TableBorderScope | null = null;

  activeTab = signal<'layout' | 'border'>('layout');

  setTab(tab: 'layout' | 'border'): void {
    this.activeTab.set(tab);
  }

  readonly lineStyles = TABLE_BORDER_LINE_STYLES;
  readonly lineWidths = TABLE_BORDER_WIDTHS;
  readonly borderColors = TABLE_BORDER_COLORS;
  readonly borderScopes = TABLE_BORDER_SCOPES;

  @Output() close = new EventEmitter<void>();

  @Output() applyBorderScope = new EventEmitter<TableBorderScope>();
  @Output() borderColorChange = new EventEmitter<string>();
  @Output() borderWidthChange = new EventEmitter<number>();
  @Output() borderStyleChange = new EventEmitter<TableBorderLineStyle>();
  @Output() clearBorders = new EventEmitter<void>();
  @Output() restoreDefaultBorders = new EventEmitter<void>();
  @Output() resetBorderSettings = new EventEmitter<void>();

  @Output() insertRowAbove = new EventEmitter<void>();
  @Output() insertRowBelow = new EventEmitter<void>();
  @Output() insertColLeft = new EventEmitter<void>();
  @Output() insertColRight = new EventEmitter<void>();

  @Output() deleteRow = new EventEmitter<void>();
  @Output() deleteCol = new EventEmitter<void>();
  @Output() deleteTable = new EventEmitter<void>();

  @Output() mergeCells = new EventEmitter<void>();
  @Output() splitCell = new EventEmitter<void>();
  @Output() splitTable = new EventEmitter<void>();

  @Output() autoFitContents = new EventEmitter<void>();
  @Output() autoFitWindow = new EventEmitter<void>();
  @Output() fixedWidth = new EventEmitter<void>();

  @Output() distributeRows = new EventEmitter<void>();
  @Output() distributeCols = new EventEmitter<void>();

  @Output() toggleGridLines = new EventEmitter<void>();

  @Output() setCellColor = new EventEmitter<string>();
  @Output() clearCellColor = new EventEmitter<void>();

  onPanelMouseDown(event: MouseEvent): void {
    const target = event.target as HTMLElement;
    if (target.tagName !== 'INPUT') {
      event.preventDefault();
    }
  }

  onPickColor(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.setCellColor.emit(value);
  }

  onBorderColorInput(event: Event): void {
    this.borderColorChange.emit((event.target as HTMLInputElement).value);
  }
}
