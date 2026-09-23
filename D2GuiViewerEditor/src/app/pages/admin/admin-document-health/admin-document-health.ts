import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import {
  ConversionProbe,
  DocumentHealthReport,
  DocumentHealthService,
  HealthFinding,
  HealthSeverity,
  HealthStage,
  PdfConversionImpact,
  PdfConversionVerdict,
  ProbeStatus,
  WordOpenImpact,
  WordOpenVerdict,
} from '../../../services/document-health.service';

const ALL = 'All';

type ImpactFilter = 'All' | 'Word' | 'Pdf';

interface FindingGroup {
  stage: HealthStage;
  label: string;
  items: HealthFinding[];
}

const STAGE_ORDER: HealthStage[] = ['File', 'Package', 'Xml', 'Structure', 'Conversion'];

const STAGE_LABELS: Record<HealthStage, string> = {
  File: 'Plik (kontener ZIP)',
  Package: 'Pakiet OPC',
  Xml: 'Części XML',
  Structure: 'Struktura WordprocessingML',
  Conversion: 'Próby przetworzenia',
};

@Component({
  selector: 'd2-admin-document-health',
  standalone: true,
  imports: [FormsModule, RouterLink],
  templateUrl: './admin-document-health.html',
  styleUrl: './admin-document-health.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AdminDocumentHealthComponent {
  private readonly api = inject(DocumentHealthService);

  readonly selectedFileName = signal<string | null>(null);
  readonly includeProbes = signal(true);
  readonly isAnalyzing = signal(false);
  readonly error = signal<string | null>(null);
  readonly report = signal<DocumentHealthReport | null>(null);
  readonly copied = signal(false);

  readonly severityFilter = signal<HealthSeverity | typeof ALL>(ALL);
  readonly stageFilter = signal<HealthStage | typeof ALL>(ALL);
  readonly impactFilter = signal<ImpactFilter>(ALL);

  private readonly expandedProbes = signal<ReadonlySet<string>>(new Set<string>());
  private selectedFile: File | null = null;

  readonly stages = STAGE_ORDER;

  readonly hasFilters = computed(
    () => this.severityFilter() !== ALL || this.stageFilter() !== ALL || this.impactFilter() !== ALL,
  );

  readonly visibleFindings = computed<HealthFinding[]>(() => {
    const findings = this.report()?.findings ?? [];
    const severity = this.severityFilter();
    const stage = this.stageFilter();
    const impact = this.impactFilter();

    return findings.filter(
      (finding) =>
        (severity === ALL || finding.severity === severity) &&
        (stage === ALL || finding.stage === stage) &&
        (impact === ALL ||
          (impact === 'Word' && finding.wordImpact !== 'None') ||
          (impact === 'Pdf' && finding.pdfImpact !== 'None')),
    );
  });

  readonly findingGroups = computed<FindingGroup[]>(() => {
    const visible = this.visibleFindings();

    return STAGE_ORDER.map((stage) => ({
      stage,
      label: STAGE_LABELS[stage],
      items: visible.filter((finding) => finding.stage === stage),
    })).filter((group) => group.items.length > 0);
  });

  readonly stageCounts = computed<Record<HealthStage, number>>(() => {
    const counts: Record<HealthStage, number> = { File: 0, Package: 0, Xml: 0, Structure: 0, Conversion: 0 };

    for (const finding of this.report()?.findings ?? []) {
      counts[finding.stage]++;
    }

    return counts;
  });

  readonly wordImpactCount = computed(
    () => (this.report()?.findings ?? []).filter((finding) => finding.wordImpact !== 'None').length,
  );

  readonly pdfImpactCount = computed(
    () => (this.report()?.findings ?? []).filter((finding) => finding.pdfImpact !== 'None').length,
  );

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.item(0) ?? null;
    input.value = '';

    if (!file) {
      return;
    }

    this.selectedFile = file;
    this.selectedFileName.set(file.name);
    this.error.set(null);
  }

  analyze(): void {
    const file = this.selectedFile;

    if (!file || this.isAnalyzing()) {
      return;
    }

    this.report.set(null);
    this.error.set(null);
    this.copied.set(false);
    this.expandedProbes.set(new Set<string>());
    this.isAnalyzing.set(true);

    this.api
      .analyze(file, this.includeProbes())
      .pipe(finalize(() => this.isAnalyzing.set(false)))
      .subscribe({
        next: (report) => this.report.set(report),
        error: (error) => this.error.set(this.readError(error)),
      });
  }

  setSeverity(severity: HealthSeverity | typeof ALL): void {
    this.severityFilter.set(this.severityFilter() === severity ? ALL : severity);
  }

  setStage(stage: HealthStage | typeof ALL): void {
    this.stageFilter.set(stage);
  }

  setImpact(impact: ImpactFilter): void {
    this.impactFilter.set(this.impactFilter() === impact ? ALL : impact);
  }

  clearFilters(): void {
    this.severityFilter.set(ALL);
    this.stageFilter.set(ALL);
    this.impactFilter.set(ALL);
  }

  isProbeExpanded(probe: ConversionProbe): boolean {
    return this.expandedProbes().has(probe.id);
  }

  toggleProbe(probe: ConversionProbe): void {
    const next = new Set(this.expandedProbes());

    if (next.has(probe.id)) {
      next.delete(probe.id);
    } else {
      next.add(probe.id);
    }

    this.expandedProbes.set(next);
  }

  async copyReport(): Promise<void> {
    const report = this.report();

    if (!report) {
      return;
    }

    try {
      await navigator.clipboard.writeText(JSON.stringify(report, null, 2));
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 2500);
    } catch {
      this.error.set('Nie udało się skopiować raportu do schowka.');
    }
  }

  verdictLabel(verdict: DocumentHealthReport['verdict']): string {
    switch (verdict) {
      case 'Healthy':
        return 'Dokument zdrowy';
      case 'Warnings':
        return 'Otwiera się, z zastrzeżeniami';
      case 'NeedsRepair':
        return 'Wymaga naprawy przez Worda';
      default:
        return 'Plik uszkodzony';
    }
  }

  wordOpenLabel(verdict: WordOpenVerdict): string {
    switch (verdict) {
      case 'Ok':
        return 'Otworzy bez naprawy';
      case 'Repair':
        return 'Zażąda naprawy';
      default:
        return 'Nie otworzy';
    }
  }

  pdfLabel(verdict: PdfConversionVerdict): string {
    switch (verdict) {
      case 'Ok':
        return 'Powinna się udać';
      case 'AtRisk':
        return 'Ryzyko różnic';
      case 'Likely':
        return 'Prawdopodobny błąd';
      default:
        return 'Zablokowana';
    }
  }

  stageLabel(stage: HealthStage): string {
    return STAGE_LABELS[stage];
  }

  severityLabel(severity: HealthSeverity): string {
    switch (severity) {
      case 'Error':
        return 'Błąd';
      case 'Warning':
        return 'Ostrzeżenie';
      default:
        return 'Info';
    }
  }

  wordImpactLabel(impact: WordOpenImpact): string | null {
    switch (impact) {
      case 'Repair':
        return 'Word: naprawa';
      case 'CannotOpen':
        return 'Word: nie otworzy';
      default:
        return null;
    }
  }

  pdfImpactLabel(impact: PdfConversionImpact): string | null {
    switch (impact) {
      case 'Possible':
        return 'PDF: możliwe różnice';
      case 'Likely':
        return 'PDF: prawdopodobny błąd';
      case 'Blocking':
        return 'PDF: blokada';
      default:
        return null;
    }
  }

  probeStatusLabel(status: ProbeStatus): string {
    switch (status) {
      case 'Passed':
        return 'OK';
      case 'Warning':
        return 'Ostrzeżenie';
      case 'Failed':
        return 'Błąd';
      default:
        return 'Pominięto';
    }
  }

  formatBytes(bytes: number): string {
    if (!bytes) {
      return '0 B';
    }

    const units = ['B', 'KB', 'MB', 'GB'];
    const unitIndex = Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)));
    return `${parseFloat((bytes / Math.pow(1024, unitIndex)).toFixed(1))} ${units[unitIndex]}`;
  }

  formatDuration(ms: number): string {
    return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)} s`;
  }

  private readError(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
      const body = error.error as { error?: string; detail?: string } | string | null;

      if (typeof body === 'string' && body.trim()) {
        return body;
      }

      if (body && typeof body === 'object') {
        return body.error ?? body.detail ?? `Błąd ${error.status}: ${error.statusText}`;
      }

      return error.status === 0
        ? 'Brak połączenia z backendem.'
        : `Błąd ${error.status}: ${error.statusText}`;
    }

    return error instanceof Error ? error.message : 'Nieznany błąd analizy.';
  }
}
