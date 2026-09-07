import { Component, EventEmitter, Input, Output, computed, signal, ChangeDetectionStrategy } from '@angular/core';
import { FormsModule } from '@angular/forms';

export type ImagePositionMode = 'inline' | 'square' | 'topBottom' | 'front' | 'behind';

export type ImageBorderStyle = 'solid' | 'dashed' | 'dotted';

export interface ImageBorderState {
  enabled: boolean;
  color: string;
  widthPx: number;
  style: ImageBorderStyle;
}

export interface ImageCropState {
  left: number;
  right: number;
  top: number;
  bottom: number;
}

export interface ImageSelectionState {
  widthPx: number;
  heightPx: number;
  aspectRatio: number;
  alignment: 'left' | 'center' | 'right' | null;
  positionMode: ImagePositionMode;
  border: ImageBorderState;
  crop: ImageCropState;
}

@Component({
  selector: 'd2-image-properties-panel',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './image-properties-panel.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './image-properties-panel.scss',
})
export class ImagePropertiesPanelComponent {
  @Input() set state(value: ImageSelectionState | null) {
    this._state.set(value);
    if (value) {
      this._widthInput.set(Math.round(value.widthPx));
      this._heightInput.set(Math.round(value.heightPx));
    }
  }

  @Input() lockAspect = true;

  @Output() close = new EventEmitter<void>();
  @Output() widthChange = new EventEmitter<number>();
  @Output() heightChange = new EventEmitter<number>();
  @Output() lockAspectChange = new EventEmitter<boolean>();
  @Output() alignmentChange = new EventEmitter<'left' | 'center' | 'right' | null>();
  @Output() removeImage = new EventEmitter<void>();
  @Output() resetAspect = new EventEmitter<void>();
  @Output() positionModeChange = new EventEmitter<ImagePositionMode>();
  @Output() borderChange = new EventEmitter<ImageBorderState>();
  @Output() cropChange = new EventEmitter<ImageCropState>();
  @Output() resetCrop = new EventEmitter<void>();

  private readonly _state = signal<ImageSelectionState | null>(null);
  protected readonly _widthInput = signal<number>(0);
  protected readonly _heightInput = signal<number>(0);

  protected readonly current = computed(() => this._state());

  protected applyWidth(): void {
    const w = this.clampPx(this._widthInput());
    this._widthInput.set(w);
    this.widthChange.emit(w);
  }

  protected applyHeight(): void {
    const h = this.clampPx(this._heightInput());
    this._heightInput.set(h);
    this.heightChange.emit(h);
  }

  protected toggleLock(checked: boolean): void {
    this.lockAspectChange.emit(checked);
  }

  protected align(value: 'left' | 'center' | 'right'): void {
    this.alignmentChange.emit(value);
  }

  protected clearAlign(): void {
    this.alignmentChange.emit(null);
  }

  protected setMode(mode: ImagePositionMode): void {
    this.positionModeChange.emit(mode);
  }

  protected setBorder(patch: Partial<ImageBorderState>): void {
    const cur = this.current()?.border;
    if (!cur) return;
    this.borderChange.emit({ ...cur, ...patch });
  }

  protected setCrop(side: 'left' | 'right' | 'top' | 'bottom', value: number): void {
    const cur = this.current()?.crop;
    if (!cur) return;
    const safe = Math.max(0, Math.min(95, Math.round(value)));
    this.cropChange.emit({ ...cur, [side]: safe });
  }

  protected resetCropClick(): void {
    this.resetCrop.emit();
  }

  private clampPx(value: number): number {
    if (!Number.isFinite(value) || value <= 0) return 16;
    return Math.max(16, Math.round(value));
  }
}
