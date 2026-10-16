import { NgTemplateOutlet } from '@angular/common';
import { Component, OnDestroy, computed, inject, signal } from '@angular/core';
import { Subscription, finalize } from 'rxjs';
import { LetterInspectionsService } from '../../core/letter-inspections.service';
import {
  ElementResult,
  ENVELOPE_LABEL,
  ENVELOPE_TYPES,
  EnvelopeType,
  Finding,
  LetterInspection,
  MAIL_KIND_LABEL,
  MAX_FILE_SIZE_BYTES,
  OUTCOME_LABEL,
} from '../../core/models';

export type Variant = 'upload' | 'order' | 'form';

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

// Jeden ekran: koperta, wariant, formularz wariantu, potem status i wynik. Plik sprawdza się sam po wczytaniu;
// w pozostałych wariantach proces uruchamia przycisk. Po uruchomieniu formularz znika do czasu „Weryfikuj kolejny”
// (po wyniku) albo „Wróć do formularza” (po błędzie), więc nie da się uruchomić dwóch żądań naraz.
@Component({
  selector: 'app-letter-check',
  imports: [NgTemplateOutlet],
  templateUrl: './letter-check.html',
  styleUrl: './letter-check.scss',
})
export class LetterCheck implements OnDestroy {
  private readonly api = inject(LetterInspectionsService);

  protected readonly envelopes = ENVELOPE_TYPES;
  protected readonly envelopeLabel = ENVELOPE_LABEL;
  protected readonly outcomeLabel = OUTCOME_LABEL;
  // Kontekst ng-template jest nietypowany, stąd Record<string, string>.
  protected readonly mailKindLabel: Record<string, string> = MAIL_KIND_LABEL;

  protected readonly variant = signal<Variant>('upload');
  protected readonly envelope = signal<EnvelopeType>('SingleWindow');
  protected readonly recipientText = signal('');
  protected readonly registeredNumber = signal('');
  protected readonly source = signal('');
  protected readonly formId = signal('');

  protected readonly busy = signal(false);
  protected readonly stage = signal('');
  protected readonly error = signal<string | null>(null);
  protected readonly result = signal<LetterInspection | null>(null);

  protected readonly recipientLines = computed(() =>
    this.recipientText()
      .split('\n')
      .map((l) => l.trim())
      .filter((l) => l.length > 0),
  );

  protected readonly canOrder = computed(() => {
    const lines = this.recipientLines().length;
    const numberOk = this.envelope() !== 'DoubleWindow' || this.registeredNumber().trim().length > 0;
    return !this.busy() && lines >= 2 && lines <= 6 && numberOk;
  });

  protected readonly canCheckForm = computed(() => !this.busy() && GUID.test(this.formId().trim()));

  protected readonly formVisible = computed(() => !this.busy() && this.error() === null && this.result() === null);

  private request?: Subscription;

  ngOnDestroy(): void {
    this.request?.unsubscribe();
  }

  protected setVariant(variant: Variant): void {
    this.variant.set(variant);
    this.reset();
  }

  protected setEnvelope(envelope: EnvelopeType): void {
    this.envelope.set(envelope);
    this.reset();
  }

  protected onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file || this.busy()) return;

    this.reset();
    const problem = LetterCheck.rejectionReason(file);
    if (problem) {
      this.error.set(problem);
      return;
    }

    const isDocx = file.name.toLowerCase().endsWith('.docx');
    this.start(isDocx ? 'Konwersja dokumentu i sprawdzanie…' : 'Sprawdzanie dokumentu…', this.api.upload(file, this.envelope()));
  }

  protected order(): void {
    if (!this.canOrder()) return;

    const envelope = this.envelope();
    this.reset();
    this.start(
      'Pobieranie dokumentu i sprawdzanie…',
      this.api.fromRecipient({
        envelope,
        source: this.source().trim() || null,
        recipientLines: this.recipientLines(),
        // Numer R ma znaczenie tylko dla dwóch okienek; dla jednego okienka nie jest wysyłany.
        registeredNumber: envelope === 'DoubleWindow' ? this.registeredNumber().trim() : null,
      }),
    );
  }

  protected checkForm(): void {
    if (!this.canCheckForm()) return;

    this.reset();
    this.start('Pobieranie dokumentu formularza i sprawdzanie…', this.api.fromForm({ envelope: this.envelope(), formId: this.formId().trim() }));
  }

  // Back to an empty form with the default envelope and variant.
  protected clear(): void {
    this.variant.set('upload');
    this.envelope.set('SingleWindow');
    this.clearFields();
    this.reset();
  }

  // Next document of the same kind: the envelope and variant stay, the entered data goes.
  protected next(): void {
    this.clearFields();
    this.reset();
  }

  // After a technical error the entered data stays, so a typo can be fixed.
  protected back(): void {
    this.reset();
  }

  // ------------------------------------------------------------ prezentacja

  protected statusText(status: ElementResult['status']): string {
    return OUTCOME_LABEL[status];
  }

  protected problems(findings: Finding[]): Finding[] {
    return findings.filter((f) => f.severity === 'Error' || f.severity === 'Inconclusive');
  }

  protected notes(findings: Finding[]): Finding[] {
    return findings.filter((f) => f.severity === 'Warning' || f.severity === 'Info');
  }

  protected area(a: { left: number; top: number; width: number; height: number }): string {
    const n = (v: number) => v.toLocaleString('pl-PL', { maximumFractionDigits: 1 });
    return `${n(a.left)}–${n(a.left + a.width)} mm od lewej, ${n(a.top)}–${n(a.top + a.height)} mm od góry`;
  }

  // ------------------------------------------------------------ wewnętrzne

  private start(stage: string, request: ReturnType<LetterInspectionsService['upload']>): void {
    this.busy.set(true);
    this.stage.set(stage);
    this.request = request.pipe(finalize(() => this.busy.set(false))).subscribe({
      next: (r) => this.result.set(r),
      error: (e) => this.error.set(LetterInspectionsService.errorMessage(e)),
    });
  }

  private clearFields(): void {
    this.recipientText.set('');
    this.registeredNumber.set('');
    this.source.set('');
    this.formId.set('');
  }

  private reset(): void {
    this.request?.unsubscribe();
    this.busy.set(false);
    this.error.set(null);
    this.result.set(null);
  }

  private static rejectionReason(file: File): string | null {
    const name = file.name.toLowerCase();
    if (!name.endsWith('.pdf') && !name.endsWith('.docx')) return `„${file.name}” nie jest plikiem PDF ani DOCX.`;
    if (file.size === 0) return 'Plik jest pusty.';
    if (file.size > MAX_FILE_SIZE_BYTES) return `Plik jest za duży. Dopuszczalny rozmiar to ${MAX_FILE_SIZE_BYTES / (1024 * 1024)} MB.`;
    return null;
  }
}
