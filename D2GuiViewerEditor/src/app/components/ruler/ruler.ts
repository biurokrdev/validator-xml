import {
  Component,
  Input,
  Output,
  EventEmitter,
  OnChanges,
  SimpleChanges,
  signal,
  HostListener, ChangeDetectionStrategy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { PageMargins } from '../../models/document.model';
import { CSS_PX_PER_CM } from '../../core/utils/units.util';

export interface RulerColumnSegment {
  startCm: number;
  widthCm: number;
}

@Component({
  selector: 'd2-ruler',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './ruler.html',
  styleUrl: './ruler.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class RulerComponent implements OnChanges {

  @Input() mode: 'horizontal' | 'vertical' = 'horizontal';

  @Input() margins: PageMargins = { top: 2.5, bottom: 2.5, left: 2.5, right: 2.5 };

  @Input() orientation: 'portrait' | 'landscape' = 'portrait';

  @Input() zoomLevel = 100;

  @Input() axisLengthCm: number | null = null;

  @Input() blockIndent: { start: number; end: number } | null = null;

  @Input() columnSegments: RulerColumnSegment[] | null = null;

  @Input() activeColumnIndex = 0;

  @Output() marginsChange = new EventEmitter<PageMargins>();

  @Output() blockIndentChange = new EventEmitter<{ start?: number; end?: number }>();

  @Output() dragGuideChange = new EventEmitter<{ active: boolean; axis: 'horizontal' | 'vertical'; offsetPx: number }>();

  readonly CM_TO_PX = CSS_PX_PER_CM;

  get pageWidthCm(): number { return this.orientation === 'portrait' ? 21 : 29.7; }
  get pageHeightCm(): number { return this.orientation === 'portrait' ? 29.7 : 21; }

  get axisCm(): number {
    if (this.mode === 'vertical' && this.axisLengthCm != null && this.axisLengthCm > 0) {
      return this.axisLengthCm;
    }
    return this.mode === 'horizontal' ? this.pageWidthCm : this.pageHeightCm;
  }

  get axisPx(): number {
    return Math.round(this.axisCm * this.CM_TO_PX);
  }

  get axisPxScaled(): number {
    return Math.round(this.axisPx * (this.zoomLevel / 100));
  }

  get scale(): number {
    return this.zoomLevel / 100;
  }

  get ticks(): number[] {
    const n = Math.floor(this.axisCm) + 1;
    if (this._ticksCache.length !== n) this._ticksCache = Array.from({ length: n }, (_, i) => i);
    return this._ticksCache;
  }
  private _ticksCache: number[] = [];

  get activeSegment(): { start: number; width: number } | null {
    if (this.mode !== 'horizontal' || !this.columnSegments || this.columnSegments.length < 2) {
      return null;
    }
    const i = Math.max(0, Math.min(this.columnSegments.length - 1, this.activeColumnIndex));
    const s = this.columnSegments[i];
    return { start: s.startCm, width: s.widthCm };
  }

  private get segStartCm(): number {
    const seg = this.activeSegment;
    if (seg) return seg.start;
    return this.mode === 'horizontal' ? this.activeMargins.left : this.activeMargins.top;
  }

  private get segEndCm(): number {
    const seg = this.activeSegment;
    if (seg) return this.axisCm - (seg.start + seg.width);
    return this.mode === 'horizontal' ? this.activeMargins.right : this.activeMargins.bottom;
  }

  get startMarginPx(): number {
    const indentCm = this.mode === 'horizontal' && this.activeBlockIndent ? this.activeBlockIndent.start : 0;
    return (this.segStartCm + indentCm) * this.CM_TO_PX * this.scale;
  }

  get endMarginPx(): number {
    const indentCm = this.mode === 'horizontal' && this.activeBlockIndent ? this.activeBlockIndent.end : 0;
    return (this.segEndCm + indentCm) * this.CM_TO_PX * this.scale;
  }

  get hGrayZones(): { left: number; width: number }[] {
    const segs = this.columnSegments;
    if (this.mode === 'horizontal' && segs && segs.length >= 2) {
      const zones: { left: number; width: number }[] = [];
      const px = (cm: number) => cm * this.CM_TO_PX * this.scale;
      let cursor = 0;
      for (const s of segs) {
        const left = px(s.startCm);
        if (left > cursor + 0.5) zones.push({ left: cursor, width: left - cursor });
        cursor = px(s.startCm + s.widthCm);
      }
      if (cursor < this.axisPxScaled - 0.5) {
        zones.push({ left: cursor, width: this.axisPxScaled - cursor });
      }
      return zones;
    }
    return [
      { left: 0, width: this.startMarginPx },
      { left: this.axisPxScaled - this.endMarginPx, width: this.endMarginPx }
    ];
  }

  private _dragging: 'start' | 'end' | null = null;
  private _dragStartClientPx = 0;
  private _dragStartMarginCm = 0;
  isDragging = signal(false);
  dragIndicatorPos = signal(0);
  activeSide = signal<'start' | 'end' | null>(null);

  private _tempMargins: PageMargins | null = null;
  private _tempBlockIndent: { start: number; end: number } | null = null;

  get activeMargins(): PageMargins {
    return this._tempMargins ?? this.margins;
  }

  get activeBlockIndent(): { start: number; end: number } | null {
    return this._tempBlockIndent ?? this.blockIndent;
  }

  private get isParagraphIndentMode(): boolean {
    return this.mode === 'horizontal' && this.blockIndent != null;
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['margins']) {
      this._tempMargins = null;
    }
    if (changes['blockIndent']) {
      this._tempBlockIndent = null;
    }
  }

  onStartHandleDown(e: MouseEvent): void {
    e.preventDefault();
    e.stopPropagation();
    const seg = this.activeSegment;
    const baseCm = seg ? seg.start : (this.mode === 'horizontal' ? this.margins.left : this.margins.top);
    const indentCm = this.isParagraphIndentMode ? (this.blockIndent?.start ?? 0) : 0;
    this._begin('start', this.mode === 'horizontal' ? e.clientX : e.clientY, baseCm + indentCm);
  }

  onEndHandleDown(e: MouseEvent): void {
    e.preventDefault();
    e.stopPropagation();
    const seg = this.activeSegment;
    const baseCm = seg
      ? this.axisCm - (seg.start + seg.width)
      : (this.mode === 'horizontal' ? this.margins.right : this.margins.bottom);
    const indentCm = this.isParagraphIndentMode ? (this.blockIndent?.end ?? 0) : 0;
    this._begin('end', this.mode === 'horizontal' ? e.clientX : e.clientY, baseCm + indentCm);
  }

  @HostListener('document:mousemove', ['$event'])
  onMouseMove(e: MouseEvent): void {
    if (!this._dragging) return;
    e.preventDefault();

    const client = this.mode === 'horizontal' ? e.clientX : e.clientY;
    const deltaCm = (client - this._dragStartClientPx) / (this.CM_TO_PX * this.scale);

    let newCm = this._dragging === 'start'
      ? this._dragStartMarginCm + deltaCm
      : this._dragStartMarginCm - deltaCm;

    const seg = this.activeSegment;
    const baseStartCm = seg ? seg.start : (this.mode === 'horizontal' ? this.margins.left : this.margins.top);
    const baseEndCm = seg
      ? this.axisCm - (seg.start + seg.width)
      : (this.mode === 'horizontal' ? this.margins.right : this.margins.bottom);

    const oppositePosition = this._dragging === 'start'
      ? baseEndCm + (this.mode === 'horizontal' && this.isParagraphIndentMode ? (this.blockIndent?.end ?? 0) : 0)
      : baseStartCm + (this.mode === 'horizontal' && this.isParagraphIndentMode ? (this.blockIndent?.start ?? 0) : 0);

    const minContentCm = seg ? Math.min(1, Math.max(0.2, seg.width - 0.2)) : 1;

    if (this.isParagraphIndentMode) {
      const minCm = seg ? (this._dragging === 'start' ? baseStartCm : baseEndCm) : 0.1;
      newCm = Math.max(minCm, Math.min(this.axisCm - oppositePosition - minContentCm, newCm));
    } else {
      newCm = Math.max(0.3, Math.min(this.axisCm - oppositePosition - minContentCm, newCm));
    }
    newCm = Math.round(newCm * 100) / 100;

    if (this.isParagraphIndentMode) {
      const pageMarginCm = this._dragging === 'start' ? baseStartCm : baseEndCm;
      const minIndentCm = seg ? 0 : -pageMarginCm + 0.1;
      const indentCm = Math.max(minIndentCm, Math.round((newCm - pageMarginCm) * 100) / 100);
      const base = this.activeBlockIndent ?? { start: 0, end: 0 };
      this._tempBlockIndent = this._dragging === 'start'
        ? { ...base, start: indentCm }
        : { ...base, end: indentCm };
      this.dragIndicatorPos.set(newCm * this.CM_TO_PX * this.scale * (this._dragging === 'start' ? 1 : 0) +
        (this._dragging === 'end' ? this.axisPxScaled - newCm * this.CM_TO_PX * this.scale : 0));
      this._emitGuide(true);
      return;
    }

    const key = this._dragging === 'start'
      ? (this.mode === 'horizontal' ? 'left' : 'top')
      : (this.mode === 'horizontal' ? 'right' : 'bottom');

    this._tempMargins = { ...this.margins, [key]: newCm };
    this.dragIndicatorPos.set(
      this._dragging === 'start' ? newCm * this.CM_TO_PX * this.scale : this.axisPxScaled - newCm * this.CM_TO_PX * this.scale
    );
    this._emitGuide(true);
  }

  @HostListener('document:mouseup')
  onMouseUp(): void {
    if (!this._dragging) return;
    if (this.isParagraphIndentMode) {
      if (this._tempBlockIndent) {
        const side = this._dragging === 'start' ? { start: this._tempBlockIndent.start } : { end: this._tempBlockIndent.end };
        this.blockIndentChange.emit(side);
      }
    } else if (this._tempMargins) {
      this.marginsChange.emit({ ...this._tempMargins });
    }
    this._dragging = null;
    this.isDragging.set(false);
    this.activeSide.set(null);
    this._tempMargins = null;
    this._tempBlockIndent = null;
    this._emitGuide(false);
  }

  private _begin(side: 'start' | 'end', clientStart: number, cm: number): void {
    this._dragging = side;
    this._dragStartClientPx = clientStart;
    this._dragStartMarginCm = cm;
    this.isDragging.set(true);
    this.activeSide.set(side);
    this.dragIndicatorPos.set(
      side === 'start' ? cm * this.CM_TO_PX * this.scale : this.axisPxScaled - cm * this.CM_TO_PX * this.scale
    );
    this._emitGuide(true);
  }

  private _emitGuide(active: boolean): void {
    this.dragGuideChange.emit({
      active,
      axis: this.mode,
      offsetPx: this.dragIndicatorPos() / Math.max(this.scale, 0.0001)
    });
  }

  tickPos(cm: number): number {
    return cm * this.CM_TO_PX * this.scale;
  }

  tickLabel(cm: number): string {
    return cm > 0 && cm < this.axisCm ? `${cm}` : '';
  }

  get startTooltip(): string {
    if (this.isParagraphIndentMode) {
      const v = (this.activeBlockIndent?.start ?? 0).toFixed(2);
      return `Wcięcie z lewej: ${v} cm`;
    }
    const s = this.mode === 'horizontal' ? 'Lewy' : 'Górny';
    const v = (this.mode === 'horizontal' ? this.activeMargins.left : this.activeMargins.top).toFixed(2);
    return `${s} margines: ${v} cm`;
  }

  get endTooltip(): string {
    if (this.isParagraphIndentMode) {
      const v = (this.activeBlockIndent?.end ?? 0).toFixed(2);
      return `Wcięcie z prawej: ${v} cm`;
    }
    const s = this.mode === 'horizontal' ? 'Prawy' : 'Dolny';
    const v = (this.mode === 'horizontal' ? this.activeMargins.right : this.activeMargins.bottom).toFixed(2);
    return `${s} margines: ${v} cm`;
  }
}
