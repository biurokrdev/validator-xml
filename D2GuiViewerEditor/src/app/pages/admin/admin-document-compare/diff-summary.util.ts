import { DifferenceKind, DocumentComparisonReport, DocumentDifference } from '../../../services/document-compare.service';
import type { DifferenceBucket } from './diff-assessment.util';

export interface ValueSample {
  left: string | null;
  right: string | null;
  count: number;
}

export interface DifferenceGroup {
  key: string;
  kind: DifferenceKind;
  partPath: string;
  category: string;
  element: string | null;
  name: string | null;
  count: number;
  sampleLeftPath: string | null;
  sampleRightPath: string | null;
  sampleContext: string | null;
  valueSamples: ValueSample[];
  verdictCounts: Record<string, number>;
  dominantVerdict: string | null;
  dominantReason: string | null;
  dominantCause: string | null;
  worstImpact: string | null;
  bucket: DifferenceBucket | null;
  bucketCounts: Record<string, number>;
  codePointer: string | null;
  featureKey: string | null;
}

const IMPACT_SEVERITY = ['DataLoss', 'WordRepair', 'PdfDifference', 'Layout', 'Cosmetic', 'None'];
const BUCKET_SEVERITY: DifferenceBucket[] = ['fix', 'review', 'noise'];

const MAX_VALUE_SAMPLES = 3;

export function impactRank(impact: string | null | undefined): number {
  const index = impact ? IMPACT_SEVERITY.indexOf(impact) : -1;
  return index === -1 ? IMPACT_SEVERITY.length : index;
}

export interface GroupingOptions {
  assess?: (difference: DocumentDifference) => { verdict: string; reason: string };
  verdictOrder?: readonly string[];
  bucket?: (difference: DocumentDifference, assessment: { verdict: string; reason: string }) => DifferenceBucket;
}

export function elementNameOf(difference: DocumentDifference): string | null {
  const path = difference.rightPath ?? difference.leftPath;

  if (!path) {
    return null;
  }

  const segment = path.substring(path.lastIndexOf('/') + 1);
  return segment.replace(/\[\d+\]$/, '') || null;
}

export function groupKeyOf(difference: DocumentDifference): string {
  return [difference.kind, difference.partPath, elementNameOf(difference) ?? '', difference.name ?? ''].join('|');
}

export function groupDifferences(differences: readonly DocumentDifference[], options: GroupingOptions = {}): DifferenceGroup[] {
  const groups = new Map<string, DifferenceGroup & {
    values: Map<string, ValueSample>;
    reasons: Map<string, string>;
    causes: Map<string, number>;
    impacts: Set<string>;
  }>();
  const order = options.verdictOrder ?? [];

  for (const difference of differences) {
    const key = groupKeyOf(difference);
    let group = groups.get(key);

    if (!group) {
      group = {
        key,
        kind: difference.kind,
        partPath: difference.partPath,
        category: difference.category,
        element: elementNameOf(difference),
        name: difference.name,
        count: 0,
        sampleLeftPath: difference.leftPath,
        sampleRightPath: difference.rightPath,
        sampleContext: difference.leftContext ?? difference.rightContext,
        valueSamples: [],
        verdictCounts: {},
        dominantVerdict: null,
        dominantReason: null,
        dominantCause: null,
        worstImpact: null,
        bucket: null,
        bucketCounts: {},
        codePointer: null,
        featureKey: null,
        values: new Map<string, ValueSample>(),
        reasons: new Map<string, string>(),
        causes: new Map<string, number>(),
        impacts: new Set<string>(),
      };
      groups.set(key, group);
    }

    group.count++;

    if (difference.analysis) {
      group.causes.set(difference.analysis.cause, (group.causes.get(difference.analysis.cause) ?? 0) + 1);
      group.impacts.add(difference.analysis.impact);
      group.codePointer ??= difference.analysis.codePointer;
      group.featureKey ??= difference.analysis.featureKey;
    }

    const assessment = options.assess?.(difference) ?? { verdict: '', reason: '' };

    if (options.assess) {
      group.verdictCounts[assessment.verdict] = (group.verdictCounts[assessment.verdict] ?? 0) + 1;

      if (!group.reasons.has(assessment.verdict)) {
        group.reasons.set(assessment.verdict, assessment.reason);
      }
    }

    if (options.bucket) {
      const bucket = options.bucket(difference, assessment);
      group.bucketCounts[bucket] = (group.bucketCounts[bucket] ?? 0) + 1;
    }

    if (!group.sampleContext) {
      group.sampleContext = difference.leftContext ?? difference.rightContext;
    }

    if (difference.leftValue !== null || difference.rightValue !== null) {
      const valueKey = `${difference.leftValue ?? '\u0000'}\u0001${difference.rightValue ?? '\u0000'}`;
      const sample = group.values.get(valueKey);

      if (sample) {
        sample.count++;
      } else {
        group.values.set(valueKey, { left: difference.leftValue, right: difference.rightValue, count: 1 });
      }
    }
  }

  return [...groups.values()]
    .map(({ values, reasons, causes, impacts, ...group }) => {
      const present = Object.keys(group.verdictCounts);
      const dominant = order.find((verdict) => present.includes(verdict)) ?? present[0] ?? null;
      const topCause = [...causes.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] ?? null;
      const bucket = options.bucket
        ? [...BUCKET_SEVERITY]
            .filter((candidate) => (group.bucketCounts[candidate] ?? 0) > 0)
            .sort((a, b) => (group.bucketCounts[b] ?? 0) - (group.bucketCounts[a] ?? 0) || BUCKET_SEVERITY.indexOf(a) - BUCKET_SEVERITY.indexOf(b))[0] ?? null
        : null;

      return {
        ...group,
        valueSamples: [...values.values()].sort((a, b) => b.count - a.count).slice(0, MAX_VALUE_SAMPLES),
        dominantVerdict: dominant,
        dominantReason: dominant ? (reasons.get(dominant) ?? null) : null,
        dominantCause: topCause,
        worstImpact: IMPACT_SEVERITY.find((impact) => impacts.has(impact)) ?? null,
        bucket,
      };
    })
    .sort((a, b) => b.count - a.count || a.partPath.localeCompare(b.partPath) || a.key.localeCompare(b.key));
}

export type DifferenceSide = 'original' | 'compared' | 'changed';

export function sideOf(kind: DifferenceKind): DifferenceSide {
  if (kind.endsWith('InLeft')) {
    return 'original';
  }

  if (kind.endsWith('InRight')) {
    return 'compared';
  }

  return 'changed';
}

const PART_LABELS: [RegExp, string][] = [
  [/^word\/document\.xml$/i, 'Treść dokumentu'],
  [/^word\/header(\d*)\.xml$/i, 'Nagłówek $1'],
  [/^word\/footer(\d*)\.xml$/i, 'Stopka $1'],
  [/^word\/footnotes\.xml$/i, 'Przypisy dolne'],
  [/^word\/endnotes\.xml$/i, 'Przypisy końcowe'],
  [/^word\/comments\w*\.xml$/i, 'Komentarze'],
  [/^word\/styles\d*\.xml$/i, 'Style'],
  [/^word\/numbering\.xml$/i, 'Definicje list'],
  [/^word\/settings\.xml$/i, 'Ustawienia dokumentu'],
  [/^word\/webSettings\.xml$/i, 'Ustawienia WWW'],
  [/^word\/fontTable\.xml$/i, 'Tabela fontów'],
  [/^word\/theme\/.*$/i, 'Motyw (kolory i fonty)'],
  [/^word\/glossary\/.*$/i, 'Bloki konstrukcyjne'],
  [/^word\/media\/(.+)$/i, 'Obraz $1'],
  [/^word\/embeddings\/(.+)$/i, 'Obiekt osadzony $1'],
  [/^word\/fonts\/(.+)$/i, 'Font osadzony $1'],
  [/^word\/_rels\/document\.xml\.rels$/i, 'Powiązania treści (relacje)'],
  [/^_rels\/\.rels$/i, 'Powiązania pakietu (relacje główne)'],
  [/\.rels$/i, 'Powiązania części (relacje)'],
  [/^\[Content_Types\]\.xml$/i, 'Typy zawartości'],
  [/^docProps\/core\.xml$/i, 'Metadane dokumentu (autor, daty)'],
  [/^docProps\/app\.xml$/i, 'Metadane aplikacji (strony, słowa)'],
  [/^docProps\/custom\.xml$/i, 'Metadane własne'],
  [/^customXml\/.*$/i, 'Dane customXml'],
];

export function describePart(path: string): string {
  for (const [pattern, label] of PART_LABELS) {
    if (pattern.test(path)) {
      return path.replace(pattern, label).trim();
    }
  }

  return path;
}

export function describeDifference(difference: DocumentDifference): string {
  const element = elementNameOf(difference);
  const name = difference.name;
  const part = describePart(difference.partPath);
  const attributeOn = element && name ? `atrybut ${name} elementu ${element}` : `atrybut ${name ?? '?'}`;

  switch (difference.kind) {
    case 'PartOnlyInLeft':
      return `Część „${part}” istnieje tylko w oryginale — po zapisie jej nie ma.`;
    case 'PartOnlyInRight':
      return `Część „${part}” istnieje tylko w porównywanym — dodał ją zapis.`;
    case 'BinaryPartChanged':
      return `Część binarna „${part}” ma inną zawartość po zapisie.`;
    case 'ElementOnlyInLeft':
      return `Oryginał ma element ${element ?? name ?? '?'} (${part}), którego nie ma w porównywanym.`;
    case 'ElementOnlyInRight':
      return `Porównywany ma element ${element ?? name ?? '?'} (${part}), którego nie było w oryginale.`;
    case 'ElementNameChanged':
      return `W tym samym miejscu (${part}) stoi inny element: oryginał ${difference.leftValue ?? '?'}, porównywany ${difference.rightValue ?? '?'}.`;
    case 'AttributeOnlyInLeft':
      return `Oryginał ma ${attributeOn} = „${difference.leftValue ?? ''}”, w porównywanym brak tego atrybutu.`;
    case 'AttributeOnlyInRight':
      return `Porównywany ma ${attributeOn} = „${difference.rightValue ?? ''}”, w oryginale go nie było.`;
    case 'AttributeValueChanged':
      return `Inna wartość: ${attributeOn} — oryginał „${difference.leftValue ?? ''}”, porównywany „${difference.rightValue ?? ''}”.`;
    case 'TextChanged':
      return `Inny tekst w ${part}: oryginał „${difference.leftValue ?? ''}”, porównywany „${difference.rightValue ?? ''}”.`;
    case 'ElementMoved':
      return `Element ${element ?? '?'} (${part}) jest w obu plikach, ale w innym miejscu.`;
    case 'PartRenamed':
      return `Ta sama zawartość binarna pod inną ścieżką: ${difference.leftValue ?? '?'} → ${difference.rightValue ?? '?'} (bez utraty).`;
    default:
      return difference.kind;
  }
}

export function describeGroup(group: DifferenceGroup): string {
  const element = group.element ?? group.name ?? '?';
  const attribute = group.name ?? '?';
  const owner = group.element ? ` na ${group.element}` : '';
  const sample = group.valueSamples[0];
  const pair = sample ? `: ${sample.left ?? '— brak —'} → ${sample.right ?? '— brak —'}` : '';

  switch (group.kind) {
    case 'PartOnlyInLeft':
      return 'Cała część zniknęła po zapisie';
    case 'PartOnlyInRight':
      return 'Zapis dodał całą część, której nie było w oryginale';
    case 'PartRenamed':
      return 'Ta sama zawartość zapisana pod inną ścieżką (bez utraty)';
    case 'BinaryPartChanged':
      return 'Zawartość binarna jest inna po zapisie';
    case 'ElementOnlyInLeft':
      return `Po zapisie brak: ${element}`;
    case 'ElementOnlyInRight':
      return `Zapis dodał: ${element}`;
    case 'AttributeOnlyInLeft':
      return `Po zapisie brak atrybutu ${attribute}${owner}${sample?.left ? ` (było: ${sample.left})` : ''}`;
    case 'AttributeOnlyInRight':
      return `Zapis dodał atrybut ${attribute}${owner}${sample?.right ? ` = ${sample.right}` : ''}`;
    case 'AttributeValueChanged':
      return `Inna wartość ${group.element ? `${group.element}/@` : ''}${attribute}${pair}`;
    case 'TextChanged':
      return `Inny tekst${pair}`;
    case 'ElementNameChanged':
      return `Inny element w tym samym miejscu${pair}`;
    case 'ElementMoved':
      return `${element} stoi w innym miejscu niż w oryginale`;
    default:
      return group.kind;
  }
}

export function groupTarget(group: DifferenceGroup): string {
  if (group.element && group.name && group.name !== group.element) {
    return `${group.element}/@${group.name}`;
  }

  return group.element ?? group.name ?? group.partPath;
}

const NOTE_BUCKET_TITLES: Record<DifferenceBucket, string> = {
  fix: 'Do naprawy u nas',
  review: 'Do sprawdzenia',
  noise: 'Nieistotne',
};

export function buildCompareNote(
  report: DocumentComparisonReport,
  groups: readonly DifferenceGroup[],
  kindLabel: (kind: DifferenceKind) => string,
): string {
  const lines: string[] = [];

  lines.push('# Porównanie dokumentów — podsumowanie wg przyczyny');
  lines.push('');
  lines.push(`Oryginał: \`${report.left.fileName}\` (${report.left.sizeInBytes} B, ${report.left.partCount} części) · Porównywany: \`${report.right.fileName}\` (${report.right.sizeInBytes} B, ${report.right.partCount} części).`);
  lines.push(`Różnic: ${report.totalDifferences}${report.truncated ? ` (lista przycięta — zgrupowano ${report.differences.length})` : ''}; grup przyczyn: ${groups.length}; pominięto atrybutów rsid: ${report.ignoredAttributeCount}.`);
  lines.push('');

  const buckets: (DifferenceBucket | null)[] = groups.some((group) => group.bucket) ? [...BUCKET_SEVERITY, null] : [null];

  for (const bucket of buckets) {
    const inBucket = groups.filter((group) => (group.bucket ?? null) === bucket);

    if (inBucket.length === 0) {
      continue;
    }

    if (bucket) {
      const total = inBucket.reduce((sum, group) => sum + group.count, 0);
      lines.push(`## ${NOTE_BUCKET_TITLES[bucket]} (${inBucket.length} przyczyn, ${total} różnic)`);
      lines.push('');
    }

    for (const group of inBucket) {
      lines.push(`### ${group.count}× ${kindLabel(group.kind)} · \`${groupTarget(group)}\` · ${group.partPath}`);
      lines.push(`- Co: ${describeGroup(group)} (${describePart(group.partPath)})`);
      lines.push(`- Kategoria: ${group.category}`);

      if (group.dominantCause) {
        lines.push(`- Przyczyna: **${group.dominantCause}** · skutek: **${group.worstImpact ?? '?'}**`);
      }

      if (group.codePointer) {
        lines.push(`- Kod: \`${group.codePointer}\``);
      }

      if (group.dominantVerdict) {
        const counts = Object.entries(group.verdictCounts).map(([verdict, count]) => `${verdict} ${count}`).join(', ');
        lines.push(`- Ocena: **${group.dominantVerdict}** (${counts}) — ${group.dominantReason ?? ''}`);
      }

      if (group.sampleLeftPath || group.sampleRightPath) {
        lines.push(`- Przykład: L \`${group.sampleLeftPath ?? '—'}\` / P \`${group.sampleRightPath ?? '—'}\``);
      }

      if (group.sampleContext) {
        lines.push(`- Akapit: „${group.sampleContext}”`);
      }

      for (const sample of group.valueSamples) {
        lines.push(`- ${sample.count}× \`${sample.left ?? '— brak —'}\` → \`${sample.right ?? '— brak —'}\``);
      }

      lines.push('');
    }
  }

  lines.push('_Grupa = rodzaj różnicy + część pakietu + element (ostatni segment ścieżki) + nazwa atrybutu/elementu. „Oryginał” = pierwszy wskazany plik, „porównywany” = drugi (np. zapis z edytora); w JSON-ie to odpowiednio left/right._');

  return lines.join('\n');
}
