import { describe, expect, it } from 'vitest';
import { buildCompareNote, describeDifference, describeGroup, describePart, elementNameOf, groupDifferences, groupTarget, impactRank, sideOf } from './diff-summary.util';
import { DocumentComparisonReport, DocumentDifference } from '../../../services/document-compare.service';

function difference(overrides: Partial<DocumentDifference>): DocumentDifference {
  return {
    kind: 'AttributeOnlyInRight',
    category: 'Formatowanie znaku',
    partPath: 'word/document.xml',
    leftPath: null,
    rightPath: '/w:document[1]/w:body[1]/w:p[1]/w:r[1]/w:rPr[1]/w:rFonts[1]',
    leftLine: null,
    rightLine: 5,
    name: 'w:hint',
    leftValue: null,
    rightValue: 'default',
    leftExcerpt: null,
    rightExcerpt: null,
    excerptTruncated: false,
    leftContext: 'Pierwszy akapit',
    rightContext: null,
    ...overrides,
  };
}

describe('diff-summary.util', () => {
  it('wyciąga element z ostatniego segmentu ścieżki bez indeksu', () => {
    expect(elementNameOf(difference({}))).toBe('w:rFonts');
    expect(elementNameOf(difference({ rightPath: null, leftPath: '/w:document[1]/w:body[1]/w:tbl[12]' }))).toBe('w:tbl');
    expect(elementNameOf(difference({ rightPath: null, leftPath: null }))).toBeNull();
  });

  it('grupuje po rodzaju, części, elemencie i nazwie; sortuje od najliczniejszych', () => {
    const groups = groupDifferences([
      difference({ rightPath: '/w:document[1]/w:body[1]/w:p[1]/w:r[1]/w:rPr[1]/w:rFonts[1]' }),
      difference({ rightPath: '/w:document[1]/w:body[1]/w:p[2]/w:r[1]/w:rPr[1]/w:rFonts[1]', leftContext: null }),
      difference({ rightPath: '/w:document[1]/w:body[1]/w:p[9]/w:r[3]/w:rPr[1]/w:rFonts[1]' }),
      difference({ kind: 'AttributeValueChanged', name: 'w:val', rightPath: '/w:document[1]/w:body[1]/w:p[1]/w:r[1]/w:rPr[1]/w:sz[1]', leftPath: '/w:document[1]/w:body[1]/w:p[1]/w:r[1]/w:rPr[1]/w:sz[1]', leftValue: '24', rightValue: '28' }),
      difference({ kind: 'AttributeValueChanged', name: 'w:val', rightPath: '/w:document[1]/w:body[1]/w:p[2]/w:r[1]/w:rPr[1]/w:sz[1]', leftPath: '/w:document[1]/w:body[1]/w:p[2]/w:r[1]/w:rPr[1]/w:sz[1]', leftValue: '24', rightValue: '28' }),
      difference({ kind: 'AttributeValueChanged', name: 'w:val', rightPath: '/w:document[1]/w:body[1]/w:p[3]/w:r[1]/w:rPr[1]/w:sz[1]', leftPath: '/w:document[1]/w:body[1]/w:p[3]/w:r[1]/w:rPr[1]/w:sz[1]', leftValue: '20', rightValue: '22' }),
      difference({ kind: 'ElementOnlyInLeft', partPath: 'word/styles.xml', name: 'w:style', rightPath: null, leftPath: '/w:styles[1]/w:style[40]', category: 'Style' }),
    ]);

    expect(groups.map((group) => [group.count, groupTarget(group), group.partPath])).toEqual([
      [3, 'w:rFonts/@w:hint', 'word/document.xml'],
      [3, 'w:sz/@w:val', 'word/document.xml'],
      [1, 'w:style', 'word/styles.xml'],
    ]);

    const sizes = groups[1];
    expect(sizes.valueSamples).toEqual([
      { left: '24', right: '28', count: 2 },
      { left: '20', right: '22', count: 1 },
    ]);
    expect(groups[0].sampleContext).toBe('Pierwszy akapit');
    expect(groups[0].sampleRightPath).toContain('w:p[1]');
  });

  it('nazywa części pakietu po ludzku i opisuje różnicę jednym zdaniem', () => {
    expect(describePart('word/document.xml')).toBe('Treść dokumentu');
    expect(describePart('word/header2.xml')).toBe('Nagłówek 2');
    expect(describePart('word/media/image1.png')).toBe('Obraz image1.png');
    expect(describePart('_rels/.rels')).toBe('Powiązania pakietu (relacje główne)');
    expect(describePart('word/unknown.xml')).toBe('word/unknown.xml');

    expect(describeDifference(difference({}))).toBe('Porównywany ma atrybut w:hint elementu w:rFonts = „default”, w oryginale go nie było.');
    expect(describeDifference(difference({ kind: 'ElementOnlyInLeft', rightPath: null, leftPath: '/w:document[1]/w:body[1]/w:p[1]/w:proofErr[1]' })))
      .toBe('Oryginał ma element w:proofErr (Treść dokumentu), którego nie ma w porównywanym.');
    expect(describeDifference(difference({ kind: 'TextChanged', leftValue: 'a', rightValue: 'b' }))).toContain('oryginał „a”, porównywany „b”');
    expect(sideOf('ElementOnlyInLeft')).toBe('original');
    expect(sideOf('AttributeOnlyInRight')).toBe('compared');
    expect(sideOf('TextChanged')).toBe('changed');
  });

  it('buduje notatkę Markdown z grupami, przykładami i parami wartości', () => {
    const differences = [
      difference({}),
      difference({ kind: 'AttributeValueChanged', name: 'w:val', leftPath: '/w:document[1]/w:body[1]/w:p[1]/w:r[1]/w:rPr[1]/w:sz[1]', rightPath: '/w:document[1]/w:body[1]/w:p[1]/w:r[1]/w:rPr[1]/w:sz[1]', leftValue: '24', rightValue: '28' }),
    ];
    const report = {
      left: { fileName: 'a.docx', sizeInBytes: 10, detectedFormat: 'docx', partCount: 3, notes: [] },
      right: { fileName: 'b.docx', sizeInBytes: 12, detectedFormat: 'docx', partCount: 3, notes: [] },
      totalDifferences: 5000,
      truncated: true,
      ignoredAttributeCount: 7,
      differences,
    } as unknown as DocumentComparisonReport;

    const note = buildCompareNote(report, groupDifferences(differences), (kind) => kind);

    expect(note).toContain('Różnic: 5000 (lista przycięta — zgrupowano 2); grup przyczyn: 2; pominięto atrybutów rsid: 7.');
    expect(note).toContain('### 1× AttributeOnlyInRight · `w:rFonts/@w:hint` · word/document.xml');
    expect(note).toContain('- Co: Zapis dodał atrybut w:hint na w:rFonts = default (Treść dokumentu)');
    expect(note).toContain('- 1× `24` → `28`');
    expect(note).toContain('- Akapit: „Pierwszy akapit”');
    expect(note).toContain('- 1× `— brak —` → `default`');
    expect(note).not.toContain('## Do naprawy u nas');
  });

  it('kubełki: grupa dostaje najczęstszy kubełek, notatka dzieli się na sekcje, tytuł grupy mówi co się stało', () => {
    const groups = groupDifferences(
      [
        difference({ kind: 'ElementOnlyInLeft', partPath: 'word/settings.xml', rightPath: null, leftPath: '/w:settings[1]/w:compat[1]', name: null, leftValue: null, rightValue: null, leftContext: null,
          analysis: { cause: 'PipelineRegenerated', impact: 'PdfDifference', explanation: 'brak w:compat', featureKey: 'settings', codePointer: 'HtmlToDocxConverter.PreserveSettings' } }),
        difference({ name: 'w:rsidR', rightPath: '/w:document[1]/w:body[1]/w:p[1]/w:r[1]', rightValue: '00A1',
          analysis: { cause: 'WordNoise', impact: 'None', explanation: 'sesja Worda', featureKey: null, codePointer: null } }),
        difference({ kind: 'TextChanged', name: null, leftPath: '/w:document[1]/w:body[1]/w:p[2]', rightPath: '/w:document[1]/w:body[1]/w:p[2]', leftValue: 'najmu', rightValue: 'sprzedaży',
          analysis: { cause: 'UserEditOrLoss', impact: 'DataLoss', explanation: 'użytkownik albo my', featureKey: null, codePointer: null } }),
      ],
      { bucket: (item) => item.analysis?.cause === 'WordNoise' ? 'noise' : item.analysis?.cause === 'UserEditOrLoss' ? 'review' : 'fix' },
    );

    expect(groups.map((group) => [group.bucket, describeGroup(group)])).toEqual([
      ['noise', 'Zapis dodał atrybut w:rsidR na w:r = 00A1'],
      ['review', 'Inny tekst: najmu → sprzedaży'],
      ['fix', 'Po zapisie brak: w:compat'],
    ]);
    expect(groups[2].codePointer).toBe('HtmlToDocxConverter.PreserveSettings');
    expect(impactRank('DataLoss')).toBeLessThan(impactRank('Cosmetic'));
    expect(impactRank(null)).toBeGreaterThan(impactRank('None'));

    const report = {
      left: { fileName: 'a.docx', sizeInBytes: 10, detectedFormat: 'docx', partCount: 3, notes: [] },
      right: { fileName: 'b.docx', sizeInBytes: 12, detectedFormat: 'docx', partCount: 3, notes: [] },
      totalDifferences: 3, truncated: false, ignoredAttributeCount: 0, differences: [],
    } as unknown as DocumentComparisonReport;
    const note = buildCompareNote(report, groups, (kind) => kind);

    expect(note.indexOf('## Do naprawy u nas (1 przyczyn, 1 różnic)')).toBeGreaterThan(0);
    expect(note.indexOf('## Do naprawy u nas')).toBeLessThan(note.indexOf('## Do sprawdzenia'));
    expect(note.indexOf('## Do sprawdzenia')).toBeLessThan(note.indexOf('## Nieistotne'));
    expect(note).toContain('- Kod: `HtmlToDocxConverter.PreserveSettings`');
  });
});
