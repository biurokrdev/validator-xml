import { describe, expect, it } from 'vitest';
import { assessDifference, bucketOf } from './diff-assessment.util';
import { DocumentDifference } from '../../../services/document-compare.service';

/**
 * Ocena różnic to heurystyki „oryginał z Worda ↔ zapis z edytora”: szum Worda i identyfikatory są
 * nieszkodliwe, dodatki writera podejrzane, straty z oryginału złe, zmiany treści do oceny.
 */
function difference(overrides: Partial<DocumentDifference>): DocumentDifference {
  return {
    kind: 'ElementOnlyInLeft',
    category: 'Tekst',
    partPath: 'word/document.xml',
    leftPath: '/w:document[1]/w:body[1]/w:p[3]',
    rightPath: null,
    leftLine: 1,
    rightLine: null,
    name: null,
    leftValue: null,
    rightValue: null,
    leftExcerpt: null,
    rightExcerpt: null,
    excerptTruncated: false,
    leftContext: null,
    rightContext: null,
    ...overrides,
  };
}

describe('bucketOf', () => {
  const analysed = (cause: NonNullable<DocumentDifference['analysis']>['cause'], impact: NonNullable<DocumentDifference['analysis']>['impact']): DocumentDifference =>
    difference({ analysis: { cause, impact, explanation: '', featureKey: null, codePointer: null } });
  const neutral = { verdict: 'ok' as const, reason: '' };

  it('strata konstrukcji przez nasz pipeline = do naprawy, nawet gdy skutek kosmetyczny; bez skutku = nieistotne', () => {
    expect(bucketOf(analysed('PipelinePartial', 'Cosmetic'), neutral)).toBe('fix');
    expect(bucketOf(analysed('PipelineUnsupported', 'DataLoss'), neutral)).toBe('fix');
    expect(bucketOf(analysed('PipelinePartial', 'None'), neutral)).toBe('noise');
  });

  it('inny zapis tego samego (regeneracja/normalizacja) = do naprawy tylko od skutku „inny układ” wzwyż', () => {
    expect(bucketOf(analysed('PipelineRegenerated', 'PdfDifference'), neutral)).toBe('fix');
    expect(bucketOf(analysed('WriterNormalization', 'Layout'), neutral)).toBe('fix');
    expect(bucketOf(analysed('PipelineRegenerated', 'Cosmetic'), neutral)).toBe('noise');
    expect(bucketOf(analysed('WriterNormalization', 'None'), neutral)).toBe('noise');
  });

  it('użytkownik albo my / edycja / nieznane = do sprawdzenia; szum, identyfikatory, artefakty = nieistotne', () => {
    expect(bucketOf(analysed('UserEditOrLoss', 'DataLoss'), neutral)).toBe('review');
    expect(bucketOf(analysed('UserEdit', 'Layout'), neutral)).toBe('review');
    expect(bucketOf(analysed('Unknown', 'None'), neutral)).toBe('noise');
    expect(bucketOf(analysed('WordNoise', 'None'), neutral)).toBe('noise');
    expect(bucketOf(analysed('PairingArtifact', 'None'), neutral)).toBe('noise');
    expect(bucketOf(analysed('IdentifierRewrite', 'None'), neutral)).toBe('noise');
  });

  it('bez analizy backendu decyduje heurystyczna ocena GUI', () => {
    expect(bucketOf(difference({}), { verdict: 'bad', reason: '' })).toBe('fix');
    expect(bucketOf(difference({}), { verdict: 'suspect', reason: '' })).toBe('fix');
    expect(bucketOf(difference({}), { verdict: 'review', reason: '' })).toBe('review');
    expect(bucketOf(difference({}), { verdict: 'ok', reason: '' })).toBe('noise');
  });
});

describe('assessDifference', () => {
  it('relacje: inny Id to nieszkodliwe, a dwie różne relacje sparowane pozycyjnie to artefakt', () => {
    const paired = assessDifference(difference({
      kind: 'AttributeValueChanged', partPath: '_rels/.rels', name: 'Id',
      leftPath: '/Relationships[1]/Relationship[1]', rightPath: '/Relationships[1]/Relationship[1]',
      leftValue: 'rId3', rightValue: 'Re6b6d5a8606c4966',
      leftExcerpt: '<Relationship Id="rId3" Type=".../extended-properties" Target="docProps/app.xml"/>',
      rightExcerpt: '<Relationship Id="Re6b6d5a8606c4966" Type=".../officeDocument" Target="/word/document.xml"/>',
    }));
    expect(paired.verdict).toBe('ok');
    expect(paired.reason).toContain('Artefakt parowania');

    const sameType = assessDifference(difference({
      kind: 'AttributeValueChanged', partPath: 'word/_rels/document.xml.rels', name: 'Id',
      leftValue: 'rId5', rightValue: 'R1a2b3c',
      leftExcerpt: '<Relationship Id="rId5" Type=".../image" Target="media/image1.png"/>',
      rightExcerpt: '<Relationship Id="R1a2b3c" Type=".../image" Target="media/image1.png"/>',
    }));
    expect(sameType.verdict).toBe('ok');
    expect(sameType.reason).toContain('R+hex');
  });

  it('utrata treści z oryginału jest zła, a znaczniki pomocnicze Worda nieszkodliwe', () => {
    expect(assessDifference(difference({})).verdict).toBe('bad');
    expect(assessDifference(difference({ leftPath: '/w:document[1]/w:body[1]/w:p[3]/w:proofErr[1]' })).verdict).toBe('ok');
    expect(assessDifference(difference({ leftPath: '/w:document[1]/w:body[1]/w:p[3]/w:bookmarkStart[1]', leftExcerpt: '<w:bookmarkStart w:name="_GoBack"/>' })).verdict).toBe('ok');
    expect(assessDifference(difference({ leftPath: '/w:document[1]/w:body[1]/w:p[3]/w:bookmarkStart[1]', leftExcerpt: '<w:bookmarkStart w:name="_Toc1"/>' })).verdict).toBe('suspect');
  });

  it('dodatki writera są podejrzane: zapieczone formatowanie i nieznane elementy w treści', () => {
    const fonts = assessDifference(difference({ kind: 'ElementOnlyInRight', leftPath: null, rightPath: '/w:document[1]/w:body[1]/w:p[1]/w:r[1]/w:rPr[1]/w:rFonts[1]' }));
    expect(fonts.verdict).toBe('suspect');
    expect(fonts.reason).toContain('zapieczone inline');

    expect(assessDifference(difference({ kind: 'ElementOnlyInRight', partPath: 'word/styles.xml', leftPath: null, rightPath: '/w:styles[1]/w:style[9]' })).verdict).toBe('ok');
  });

  it('części: brak app.xml podejrzany, brak styles.xml zły, dodany numbering.xml nieszkodliwy', () => {
    expect(assessDifference(difference({ kind: 'PartOnlyInLeft', partPath: 'docProps/app.xml', leftPath: null })).verdict).toBe('suspect');
    expect(assessDifference(difference({ kind: 'PartOnlyInLeft', partPath: 'word/styles.xml', leftPath: null })).verdict).toBe('bad');
    expect(assessDifference(difference({ kind: 'PartOnlyInLeft', partPath: 'word/comments.xml', leftPath: null })).reason).toContain('KR-03');
    expect(assessDifference(difference({ kind: 'PartOnlyInRight', partPath: 'word/numbering.xml', leftPath: null })).verdict).toBe('ok');
  });

  it('wartości: zaokrąglenie jednostek i kolor auto nieszkodliwe, zmiana stylu zła, duża zmiana do oceny', () => {
    const base = { kind: 'AttributeValueChanged' as const, rightPath: '/w:document[1]/w:body[1]/w:p[1]/w:pPr[1]/w:ind[1]', leftPath: '/w:document[1]/w:body[1]/w:p[1]/w:pPr[1]/w:ind[1]' };
    expect(assessDifference(difference({ ...base, name: 'w:left', leftValue: '1417', rightValue: '1418' })).verdict).toBe('ok');
    expect(assessDifference(difference({ ...base, name: 'w:left', leftValue: '1417', rightValue: '720' })).verdict).toBe('review');
    expect(assessDifference(difference({ ...base, name: 'w:val', leftValue: 'auto', rightValue: '000000', rightPath: '/w:document[1]/w:body[1]/w:p[1]/w:r[1]/w:rPr[1]/w:color[1]' })).verdict).toBe('ok');
    expect(assessDifference(difference({ ...base, name: 'w:val', leftValue: 'Heading1', rightValue: 'Normal', leftPath: '/w:document[1]/w:body[1]/w:p[1]/w:pPr[1]/w:pStyle[1]', rightPath: '/w:document[1]/w:body[1]/w:p[1]/w:pPr[1]/w:pStyle[1]' })).verdict).toBe('bad');
    expect(assessDifference(difference({ ...base, name: 'w:rsidR', leftValue: '00A1', rightValue: '00B2' })).verdict).toBe('ok');
  });

  it('tekst: same białe znaki podejrzane, jednostronny zły, inna treść do oceny; przeniesienie w treści złe, w relacjach nieszkodliwe', () => {
    expect(assessDifference(difference({ kind: 'TextChanged', leftValue: 'a b', rightValue: 'a b' })).verdict).toBe('suspect');
    expect(assessDifference(difference({ kind: 'TextChanged', leftValue: 'tekst', rightValue: '' })).verdict).toBe('bad');
    expect(assessDifference(difference({ kind: 'TextChanged', leftValue: 'najmu', rightValue: 'sprzedaży' })).verdict).toBe('review');
    expect(assessDifference(difference({ kind: 'ElementMoved', rightPath: '/w:document[1]/w:body[1]/w:p[9]' })).verdict).toBe('bad');
    expect(assessDifference(difference({ kind: 'ElementMoved', partPath: '_rels/.rels', rightPath: '/Relationships[1]/Relationship[2]' })).verdict).toBe('ok');
  });
});
