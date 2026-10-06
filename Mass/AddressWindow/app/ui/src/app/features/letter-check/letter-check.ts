import { LowerCasePipe, NgTemplateOutlet } from '@angular/common';
import { Component, OnDestroy, inject, signal } from '@angular/core';
import { Subscription, finalize } from 'rxjs';
import { LetterInspectionsService } from '../../core/letter-inspections.service';
import {
  Address,
  ENVELOPE_HINT,
  ENVELOPE_LABEL,
  ENVELOPE_TYPES,
  EnvelopeType,
  LetterInspection,
  MAX_FILE_SIZE_BYTES,
  RegisteredLabel,
  SEVERITY_LABEL,
} from '../../core/models';

/**
 * Sprawdzenie pisma PDF pod kopertę z okienkiem. Przepływ: wybierz rodzaj koperty -> „Wczytaj plik PDF”
 * -> plik od razu idzie do API -> wynik walidacji. Zmiana rodzaju koperty sprawdza wczytany plik ponownie.
 */
@Component({
  selector: 'app-letter-check',
  imports: [NgTemplateOutlet, LowerCasePipe],
  templateUrl: './letter-check.html',
  styleUrl: './letter-check.scss',
})
export class LetterCheck implements OnDestroy {
  private readonly api = inject(LetterInspectionsService);

  protected readonly envelopes = ENVELOPE_TYPES;
  protected readonly envelopeLabel = ENVELOPE_LABEL;
  protected readonly envelopeHint = ENVELOPE_HINT;
  protected readonly severityLabel: Record<string, string> = SEVERITY_LABEL;

  protected readonly envelope = signal<EnvelopeType>('SingleWindow');
  protected readonly file = signal<File | null>(null);
  protected readonly result = signal<LetterInspection | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly checking = signal(false);

  private request?: Subscription;

  ngOnDestroy(): void {
    this.request?.unsubscribe();
  }

  protected onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    // Bez tego ponowny wybór tego samego pliku (np. po poprawce w Wordzie) nie wywołałby zdarzenia change.
    input.value = '';
    if (!file) return;

    this.request?.unsubscribe();
    this.result.set(null);

    const problem = LetterCheck.rejectionReason(file);
    if (problem) {
      this.file.set(null);
      this.error.set(problem);
      return;
    }

    this.file.set(file);
    this.inspect();
  }

  protected onEnvelopeChange(envelope: EnvelopeType): void {
    this.envelope.set(envelope);
    if (this.file()) this.inspect();
  }

  protected clear(): void {
    this.request?.unsubscribe();
    this.file.set(null);
    this.result.set(null);
    this.error.set(null);
  }

  protected fontSize(address: Address): string | null {
    const { minFontSizePt: min, maxFontSizePt: max } = address;
    if (min == null || max == null) return null;
    return min === max ? `${LetterCheck.num(min)} pt` : `${LetterCheck.num(min)}–${LetterCheck.num(max)} pt`;
  }

  protected labelSize(label: RegisteredLabel): string {
    return `${LetterCheck.num(label.bounds.width)} × ${LetterCheck.num(label.bounds.height)} mm`;
  }

  protected fileSize(bytes: number): string {
    return bytes < 1024 * 1024
      ? `${LetterCheck.num(bytes / 1024)} KB`
      : `${LetterCheck.num(bytes / (1024 * 1024))} MB`;
  }

  private inspect(): void {
    const file = this.file();
    if (!file) return;

    // Poprzednie żądanie jest porzucane, żeby spóźniona odpowiedź nie nadpisała nowszego wyniku.
    this.request?.unsubscribe();
    this.error.set(null);
    this.result.set(null);
    this.checking.set(true);

    this.request = this.api
      .inspect(file, this.envelope())
      .pipe(finalize(() => this.checking.set(false)))
      .subscribe({
        next: (r) => this.result.set(r),
        error: (e) => this.error.set(LetterInspectionsService.errorMessage(e)),
      });
  }

  /** Szybka kontrola po stronie przeglądarki; API i tak sprawdza plik samo. */
  private static rejectionReason(file: File): string | null {
    const isPdf = file.type === 'application/pdf' || file.name.toLowerCase().endsWith('.pdf');
    if (!isPdf) return `„${file.name}” nie jest plikiem PDF.`;
    if (file.size === 0) return 'Plik jest pusty.';
    if (file.size > MAX_FILE_SIZE_BYTES) {
      return `Plik jest za duży. Dopuszczalny rozmiar to ${MAX_FILE_SIZE_BYTES / (1024 * 1024)} MB.`;
    }
    return null;
  }

  private static num(value: number): string {
    return value.toLocaleString('pl-PL', { maximumFractionDigits: 1 });
  }
}
