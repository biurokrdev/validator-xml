import {
  AppSupportLevel,
  CoverageStatus,
  DocumentHealthReport,
  HealthFinding,
  ImplementationCoverageItem,
} from '../../../services/document-health.service';

const STATUS_LABELS: Record<CoverageStatus, string> = {
  UnexpectedLoss: 'NIEOCZEKIWANA UTRATA w round-tripie',
  Unsupported: 'brak obsługi',
  Unverified: 'obsługa niezweryfikowana',
  PassThrough: 'tylko podgląd (pass-through XML)',
  Partial: 'obsługa częściowa',
  Supported: 'obsługiwane',
};

const LEVEL_SHORT: Record<AppSupportLevel, string> = {
  Full: 'pełna',
  Partial: 'częściowa',
  PassThrough: 'pass-through',
  Unsupported: 'brak',
  Unknown: '?',
};

const GAP_ORDER: CoverageStatus[] = ['UnexpectedLoss', 'Unsupported', 'Unverified', 'PassThrough', 'Partial'];

const MAX_PROBE_DETAIL_LINES = 1200;

export function buildDeveloperNote(report: DocumentHealthReport): string {
  const lines: string[] = [];
  const gaps = report.coverage.filter((item) => item.status !== 'Supported');
  const appFindings = report.findings.filter((finding) => finding.stage === 'Application');
  const failedProbes = report.probes.filter((probe) => probe.status === 'Failed' || probe.status === 'Warning');
  const annotated = report.findings.filter(
    (finding) => finding.stage !== 'Application' && finding.appNote && finding.appSupport !== 'Full',
  );

  lines.push(`# Kondycja dokumentu — notatka dla programisty`);
  lines.push('');
  lines.push(`Plik: \`${report.fileName}\` (${report.detectedFormat}, ${report.fileSizeInBytes} B), analiza ${report.analyzedAtUtc}.`);
  lines.push(`Werdykt: ${report.verdict} · Word: ${report.wordOpen} · PDF: ${report.pdfConversion}.`);
  lines.push(`Pokrycie: ${report.coverageSummary}`);
  lines.push('');

  lines.push('## Luki naszej implementacji (reader → edytor → writer)');
  lines.push('');

  if (gaps.length === 0) {
    lines.push('Brak — każda konstrukcja z rejestru użyta w dokumencie jest obsługiwana w pełni.');
  } else {
    for (const status of GAP_ORDER) {
      const items = gaps.filter((item) => item.status === status);

      if (items.length === 0) {
        continue;
      }

      lines.push(`### ${STATUS_LABELS[status]} (${items.length})`);
      lines.push('');

      for (const item of items) {
        lines.push(...describeCoverageItem(item));
      }
    }
  }

  lines.push('');
  lines.push('## Ustalenia etapu „Aplikacja”');
  lines.push('');

  if (appFindings.length === 0) {
    lines.push('Brak.');
  } else {
    for (const finding of appFindings) {
      lines.push(...describeFinding(finding));
    }
  }

  if (annotated.length > 0) {
    lines.push('');
    lines.push('## Problemy pliku, z którymi nasza aplikacja radzi sobie inaczej niż Word');
    lines.push('');

    for (const finding of annotated) {
      lines.push(`- **${finding.code}** (${finding.severity}, obsługa: ${LEVEL_SHORT[finding.appSupport]}) — ${finding.title}`);
      lines.push(`  - U nas: ${finding.appNote}`);

      if (finding.location) {
        lines.push(`  - Lokalizacja: \`${finding.location}\``);
      }
    }
  }

  if (failedProbes.length > 0) {
    lines.push('');
    lines.push('## Próby przetworzenia zakończone błędem lub ostrzeżeniem');
    lines.push('');

    for (const probe of failedProbes) {
      lines.push(`- **${probe.name}** [${probe.status}]: ${probe.message ?? '—'}`);

      if (probe.details) {
        const detailLines = probe.details.split('\n');
        lines.push('  ```');
        lines.push(...detailLines.slice(0, MAX_PROBE_DETAIL_LINES).map((line) => `  ${line}`));

        if (detailLines.length > MAX_PROBE_DETAIL_LINES) {
          lines.push(`  … (${detailLines.length - MAX_PROBE_DETAIL_LINES} linii więcej w raporcie JSON)`);
        }

        lines.push('  ```');
      }
    }
  }

  lines.push('');
  lines.push('_Kolumny „reader / edytor / writer” pochodzą z rejestru EditorCapabilityRegistry (Infrastructure/Services/DocumentHealth); wynik round-tripu z próby „Edytor: round-trip DOCX → HTML → DOCX”. Rozbieżność = popraw kod albo rejestr._');

  return lines.join('\n');
}

function describeCoverageItem(item: ImplementationCoverageItem): string[] {
  const roundTrip =
    item.roundTripCount === null
      ? 'round-trip: nie uruchomiono'
      : `round-trip: ${item.sourceCount} → ${item.roundTripCount} (${item.roundTrip})`;
  const lines = [
    `- **${item.label}** \`${item.featureKey}\` — w źródle: ${item.sourceCount}; ${roundTrip}; reader ${LEVEL_SHORT[item.reader]} / edytor ${LEVEL_SHORT[item.editor]} / writer ${LEVEL_SHORT[item.writer]}`,
    `  - ${item.note}`,
  ];

  if (item.codePointer) {
    lines.push(`  - Kod: ${item.codePointer}`);
  }

  if (item.sampleLocation) {
    lines.push(`  - Przykład: \`${item.sampleLocation}\``);
  }

  return lines;
}

function describeFinding(finding: HealthFinding): string[] {
  const lines = [`- **${finding.code}** (${finding.severity}) — ${finding.title}`, `  - ${finding.description}`];

  if (finding.remedy) {
    lines.push(`  - Co zrobić: ${finding.remedy}`);
  }

  if (finding.location) {
    lines.push(`  - Lokalizacja: \`${finding.location}\``);
  }

  return lines;
}
