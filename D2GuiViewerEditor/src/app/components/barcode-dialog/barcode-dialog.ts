import { Component, EventEmitter, Output, signal, inject, ChangeDetectionStrategy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { BarcodeService, BarcodeResponse } from '../../services/barcode.service';

@Component({
  selector: 'd2-barcode-dialog',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './barcode-dialog.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './barcode-dialog.scss'
})
export class BarcodeDialogComponent {
  private barcodeService = inject(BarcodeService);
  private previewDebounce?: ReturnType<typeof setTimeout>;
  private previewRequestId = 0;

  @Output() insertBarcode = new EventEmitter<{ base64Image: string; content: string; showValueBelow: boolean }>();
  @Output() close = new EventEmitter<void>();

  barcodeTypes = this.barcodeService.barcodeTypes;

  selectedType = signal('QRCode');
  content = signal('');
  width = signal(300);
  height = signal(300);
  showValueBelow = signal(false);
  preview = signal<string | null>(null);
  isLoading = signal(false);
  errorMessage = signal<string | null>(null);

  get is2DCode(): boolean {
    return this.barcodeTypes.find(t => t.value === this.selectedType())?.is2D ?? true;
  }

  generatePreview(showValidationError: boolean = true): void {
    const contentValue = this.content();
    if (!contentValue.trim()) {
      this.errorMessage.set(showValidationError ? 'Wprowadź treść kodu' : null);
      this.preview.set(null);
      this.isLoading.set(false);
      return;
    }

    const requestId = ++this.previewRequestId;
    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.barcodeService.generateBarcode({
      content: contentValue,
      barcodeType: this.selectedType(),
      width: this.width(),
      height: this.height(),
      showText: this.showValueBelow()
    }).subscribe({
      next: (response: BarcodeResponse) => {
        if (requestId !== this.previewRequestId) return;
        this.preview.set(response.base64Image);
        this.isLoading.set(false);
      },
      error: (err) => {
        if (requestId !== this.previewRequestId) return;
        const message = err.error?.error || 'Nie udało się wygenerować kodu';
        this.errorMessage.set(message);
        this.preview.set(null);
        this.isLoading.set(false);
      }
    });
  }

  private scheduleAutoPreview(): void {
    if (this.previewDebounce) {
      clearTimeout(this.previewDebounce);
    }

    this.previewDebounce = setTimeout(() => {
      this.generatePreview(false);
    }, 300);
  }

  onInsert(): void {
    const contentValue = this.content();
    if (!contentValue.trim()) {
      this.errorMessage.set('Wprowadź treść kodu');
      return;
    }

    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.barcodeService.generateBarcode({
      content: contentValue,
      barcodeType: this.selectedType(),
      width: this.width(),
      height: this.height(),
      showText: this.showValueBelow()
    }).subscribe({
      next: (response: BarcodeResponse) => {
        this.insertBarcode.emit({
          base64Image: response.base64Image,
          content: this.content(),
          showValueBelow: this.showValueBelow()
        });
        this.isLoading.set(false);
      },
      error: (err) => {
        const message = err.error?.error || 'Nie udało się wygenerować kodu';
        this.errorMessage.set(message);
        this.isLoading.set(false);
      }
    });
  }

  onShowValueBelowChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.showValueBelow.set(input.checked);
    this.scheduleAutoPreview();
  }

  onCancel(): void {
    this.close.emit();
  }

  onTypeChange(event: Event): void {
    const select = event.target as HTMLSelectElement;
    this.selectedType.set(select.value);
    this.scheduleAutoPreview();
  }

  onContentChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.content.set(input.value);
    this.scheduleAutoPreview();
  }

  onWidthChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.width.set(parseInt(input.value, 10) || 300);
    this.scheduleAutoPreview();
  }

  onHeightChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.height.set(parseInt(input.value, 10) || 300);
    this.scheduleAutoPreview();
  }
}
