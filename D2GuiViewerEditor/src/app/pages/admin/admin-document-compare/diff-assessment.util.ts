import { DifferenceCause, DifferenceImpact, DocumentDifference } from '../../../services/document-compare.service';
import { elementNameOf } from './diff-summary.util';

export const CAUSE_LABELS: Record<DifferenceCause, string> = {
  PipelineUnsupported: 'nasz pipeline tego nie obsługuje',
  PipelinePartial: 'nasz pipeline obsługuje częściowo',
  PipelineRegenerated: 'nasz writer generuje od nowa',
  WriterNormalization: 'nasz writer zapisuje inaczej',
  UserEditOrLoss: 'użytkownik usunął albo my zgubiliśmy',
  UserEdit: 'edycja użytkownika',
  IdentifierRewrite: 'identyfikator przepisany',
  PairingArtifact: 'artefakt parowania',
  WordNoise: 'szum Worda',
  Unknown: 'przyczyna nieznana',
};

export const IMPACT_LABELS: Record<DifferenceImpact, string> = {
  DataLoss: 'utrata treści/danych',
  WordRepair: 'Word zgłosi naprawę',
  PdfDifference: 'inny PDF',
  Layout: 'inny układ/wygląd',
  Cosmetic: 'kosmetyczny',
  None: 'bez skutku',
};

export const CAUSE_ORDER: DifferenceCause[] = [
  'PipelineUnsupported', 'PipelinePartial', 'PipelineRegenerated', 'UserEditOrLoss', 'Unknown',
  'WriterNormalization', 'UserEdit', 'IdentifierRewrite', 'PairingArtifact', 'WordNoise',
];

export function verdictFromAnalysis(cause: DifferenceCause, impact: DifferenceImpact): DifferenceVerdict {
  if (cause === 'UserEditOrLoss' || cause === 'UserEdit' || cause === 'Unknown') {
    return 'review';
  }

  switch (impact) {
    case 'DataLoss':
    case 'WordRepair':
      return 'bad';
    case 'Layout':
    case 'PdfDifference':
      return 'suspect';
    case 'Cosmetic':
      return cause === 'PipelinePartial' || cause === 'PipelineUnsupported' ? 'suspect' : 'ok';
    default:
      return 'ok';
  }
}

export type DifferenceVerdict = 'ok' | 'suspect' | 'bad' | 'review';

export interface DifferenceAssessment {
  verdict: DifferenceVerdict;
  reason: string;
}

export const VERDICT_LABELS: Record<DifferenceVerdict, string> = {
  ok: 'nieszkodliwa',
  suspect: 'podejrzana',
  bad: 'zła',
  review: 'do oceny',
};

export const VERDICT_ORDER: DifferenceVerdict[] = ['bad', 'suspect', 'review', 'ok'];

export type DifferenceBucket = 'fix' | 'review' | 'noise';

export const BUCKET_ORDER: DifferenceBucket[] = ['fix', 'review', 'noise'];

export const BUCKET_LABELS: Record<DifferenceBucket, string> = {
  fix: 'Do naprawy u nas',
  review: 'Do sprawdzenia',
  noise: 'Nieistotne',
};

export const BUCKET_HINTS: Record<DifferenceBucket, string> = {
  fix: 'Nasz reader, edytor albo writer gubi lub zmienia coś, czego użytkownik nie dotykał. Każda pozycja to zadanie dla programisty.',
  review: 'Treść albo wartość jest inna, a reguły nie wiedzą, czy to celowa edycja, czy strata. Rozstrzyga człowiek z kontekstem akapitu.',
  noise: 'Identyfikatory, sesje Worda, kolejność w zbiorach nieuporządkowanych, zaokrąglenia jednostek. Bez wpływu na dokument.',
};

export function bucketOf(difference: DocumentDifference, assessment: DifferenceAssessment): DifferenceBucket {
  const analysis = difference.analysis;

  if (analysis) {
    switch (analysis.cause) {
      case 'PipelineUnsupported':
      case 'PipelinePartial':
        return analysis.impact === 'None' ? 'noise' : 'fix';
      case 'PipelineRegenerated':
      case 'WriterNormalization':
        return analysis.impact === 'None' || analysis.impact === 'Cosmetic' ? 'noise' : 'fix';
      case 'UserEditOrLoss':
      case 'UserEdit':
      case 'Unknown':
        return analysis.impact === 'None' ? 'noise' : 'review';
      default:
        return 'noise';
    }
  }

  switch (assessment.verdict) {
    case 'bad':
    case 'suspect':
      return 'fix';
    case 'review':
      return 'review';
    default:
      return 'noise';
  }
}

const CONTENT_PART = /^word\/(document|header\d*|footer\d*|footnotes|endnotes|comments|glossary\/document)\.xml$/i;
const UNORDERED_PART = /(^|\/)_rels\/|\[Content_Types\]\.xml$|^word\/(styles|numbering|fontTable|settings|webSettings)\.xml$|^docProps\//i;

const NOISE_ELEMENTS = new Set([
  'w:proofErr', 'w:lastRenderedPageBreak', 'w:noProof', 'w:lang', 'w:rsid', 'w:rsids', 'w:rsidRoot',
  'w:bookmarkStart', 'w:bookmarkEnd',
]);

const BAKED_FORMATTING = new Set([
  'w:rFonts', 'w:sz', 'w:szCs', 'w:color', 'w:spacing', 'w:jc', 'w:ind', 'w:kern', 'w:lang',
  'w:tblGrid', 'w:gridCol', 'w:tcW', 'w:tblW', 'w:tblLook', 'w:tblInd', 'w:tblCellMar', 'w:tblLayout',
  'w:tcBorders', 'w:tblBorders', 'w:shd', 'w:vAlign', 'w:widowControl', 'w:contextualSpacing', 'w:snapToGrid',
  'w:trHeight', 'w:tabs', 'w:tab', 'w:b', 'w:bCs', 'w:i', 'w:iCs',
]);

const NOISE_ATTRIBUTES = /^(w:rsid\w*|w14:paraId|w14:textId|mc:Ignorable|xml:space|w:hint|w:eastAsia|w:cs|w:bidi|w:val\.durableId|w:durableId)$/;

const UNIT_ATTRIBUTES = /^(w:w|w:h|w:left|w:right|w:top|w:bottom|w:start|w:end|w:hanging|w:firstLine|w:before|w:after|w:line|w:pos|w:sz|w:space|w:val|w:tblpX|w:tblpY|w:leftChars|cx|cy|x|y)$/;
const UNIT_TOLERANCE = 2;

export function assessDifference(difference: DocumentDifference): DifferenceAssessment {
  if (difference.analysis) {
    return {
      verdict: verdictFromAnalysis(difference.analysis.cause, difference.analysis.impact),
      reason: difference.analysis.explanation,
    };
  }

  const part = difference.partPath;
  const element = elementNameOf(difference);
  const name = difference.name ?? '';
  const inContent = CONTENT_PART.test(part);

  switch (difference.kind) {
    case 'PartOnlyInLeft':
      return assessMissingPart(part);
    case 'PartOnlyInRight':
      return assessAddedPart(part);
    case 'PartRenamed':
      return { verdict: 'ok', reason: 'Identyczne bajty pod inną ścieżką — writer nazywa części obrazów po swojemu; bez utraty.' };
    case 'BinaryPartChanged':
      return /\/media\//i.test(part)
        ? { verdict: 'suspect', reason: 'Bajty obrazu się zmieniły — writer przekodował grafikę (np. metaplik → podgląd albo inny format); sprawdź, czy oryginał nie był EMF/WMF/SVG.' }
        : { verdict: 'bad', reason: 'Zmieniła się część binarna spoza obrazów (font, OLE, podpis) — nasz zapis nie powinien jej dotykać.' };
    case 'ElementMoved':
      return inContent
        ? { verdict: 'bad', reason: 'Element treści zmienił miejsce w dokumencie — kolejność akapitów/tabel po zapisie jest inna niż w oryginale.' }
        : { verdict: 'ok', reason: 'Kolejność w tej części nie ma znaczenia (relacje, typy zawartości, style) — Word i SDK zapisują ją dowolnie.' };
    case 'ElementNameChanged':
      return { verdict: 'bad', reason: 'W tym samym miejscu stoi inny element — struktura treści po zapisie różni się od oryginału.' };
    case 'ElementOnlyInLeft':
      return assessLostElement(difference, element, inContent);
    case 'ElementOnlyInRight':
      return assessAddedElement(element, inContent, part);
    case 'AttributeOnlyInLeft':
      if (NOISE_ATTRIBUTES.test(name)) {
        return { verdict: 'ok', reason: `Atrybut ${name} to szum Worda (sesje edycji, identyfikatory, deklaracje) — jego brak niczego nie zmienia.` };
      }

      return inContent
        ? { verdict: 'bad', reason: `Atrybut ${name} z oryginału nie wrócił po zapisie — writer gubi tę właściwość (${element ?? 'element'}).` }
        : { verdict: 'suspect', reason: `Atrybut ${name} zniknął w części ${part} — poza treścią, ale writer nie powinien go pomijać.` };
    case 'AttributeOnlyInRight':
      if (NOISE_ATTRIBUTES.test(name)) {
        return { verdict: 'ok', reason: `Atrybut ${name} to szum bez wpływu na treść.` };
      }

      return { verdict: 'suspect', reason: `Writer dodał atrybut ${name} na ${element ?? 'elemencie'}, którego Word nie zapisał — zwykle jawne formatowanie (nie strata, ale odejście od oryginału).` };
    case 'AttributeValueChanged':
      return assessAttributeValue(difference, name, part, inContent);
    case 'TextChanged':
      return assessText(difference);
    default:
      return { verdict: 'review', reason: 'Nieznany rodzaj różnicy.' };
  }
}

function assessMissingPart(part: string): DifferenceAssessment {
  if (/^docProps\/app\.xml$/i.test(part)) {
    return { verdict: 'suspect', reason: 'Brak docProps/app.xml po zapisie: Word toleruje, ale giną metadane aplikacji (liczba stron/słów, program) — writer powinien ją odtwarzać.' };
  }

  if (/^docProps\//i.test(part) || /webSettings\.xml$/i.test(part) || /\.rels$/i.test(part) && !/^_rels\/\.rels$/i.test(part)) {
    return { verdict: 'ok', reason: 'Część pomocnicza (metadane, ustawienia WWW, relacje części, której nie ma) — bez wpływu na treść i otwarcie.' };
  }

  if (/^word\/(styles|theme\/theme\d*|fontTable)\.xml$/i.test(part)) {
    return { verdict: 'bad', reason: 'Część stylów/motywu/fontów z oryginału nie przetrwała — pass-through pakietu nie zadziałał (R-16): formatowanie ze stylów zginie.' };
  }

  if (/^word\/(comments|commentsExtended|commentsIds|people)\.xml$/i.test(part)) {
    return { verdict: 'bad', reason: 'Komentarze nie są obsługiwane przez nasz pipeline — cała część ginie w v2 (znana luka, KR-03).' };
  }

  if (/^customXml\//i.test(part) || /^word\/glossary\//i.test(part) || /^word\/fonts\//i.test(part) || /^word\/embeddings\//i.test(part)) {
    return { verdict: 'bad', reason: 'Część danych (customXml / bloki konstrukcyjne / osadzone fonty lub obiekty) nie jest kopiowana w pass-through — trwała strata po pierwszym zapisie.' };
  }

  if (/^word\/media\//i.test(part)) {
    return { verdict: 'bad', reason: 'Obraz z oryginału nie ma odpowiednika po zapisie — grafika zginęła albo zmieniła nazwę części bez sparowania (sprawdź relacje).' };
  }

  return { verdict: 'bad', reason: 'Część pakietu z oryginału nie istnieje po zapisie — writer jej nie odtwarza.' };
}

function assessAddedPart(part: string): DifferenceAssessment {
  if (/^word\/numbering\.xml$/i.test(part)) {
    return { verdict: 'ok', reason: 'Writer zawsze generuje numbering.xml (listy wariant A) — oryginał list nie miał albo trzymał je inaczej.' };
  }

  if (/^word\/styles\d+\.xml$/i.test(part)) {
    return { verdict: 'suspect', reason: 'Niekanoniczna nazwa części stylów (styles2.xml) — artefakt pass-through (R-21); Word czyta po relacji, ale to sygnał, że część istniała podwójnie.' };
  }

  if (/^word\/(footnotes|endnotes)\.xml$/i.test(part)) {
    return { verdict: 'ok', reason: 'Writer tworzy część przypisów, gdy dokument ma przypisy — oryginał mógł trzymać je bez osobnej części tylko przy braku przypisów; sprawdź liczniki.' };
  }

  if (/^word\/media\//i.test(part)) {
    return { verdict: 'suspect', reason: 'Nowa część obrazu po zapisie — writer zapisał grafikę pod inną nazwą/formatem (parowanie po nazwie części zawodzi); porównaj z brakującą częścią po stronie oryginału.' };
  }

  return { verdict: 'suspect', reason: 'Część, której nie było w oryginale — writer dodaje ją od siebie; sprawdź, czy jest potrzebna.' };
}

function assessLostElement(difference: DocumentDifference, element: string | null, inContent: boolean): DifferenceAssessment {
  if (element && NOISE_ELEMENTS.has(element)) {
    if ((element === 'w:bookmarkStart' || element === 'w:bookmarkEnd') && !/_GoBack/.test(difference.leftExcerpt ?? '')) {
      return { verdict: 'suspect', reason: 'Zakładka z oryginału nie wróciła po zapisie — cele PAGEREF/odsyłaczy mogą przestać działać.' };
    }

    return { verdict: 'ok', reason: `${element} to znacznik pomocniczy Worda (korekta, sesja, podział strony z renderowania) — jego brak nie zmienia dokumentu.` };
  }

  if (!inContent) {
    return { verdict: 'suspect', reason: `Element ${element ?? ''} z części ${difference.partPath} nie wrócił po zapisie — poza treścią, ale to odejście od oryginału (style/ustawienia).` };
  }

  if (element && BAKED_FORMATTING.has(element)) {
    return { verdict: 'suspect', reason: `Właściwość ${element} z oryginału nie wróciła — formatowanie mogło zostać przeniesione do stylu/domyślnych albo zgubione; porównaj wygląd akapitu.` };
  }

  return { verdict: 'bad', reason: `Element ${element ?? ''} istniał w oryginale, a po zapisie go nie ma — utrata treści lub struktury (sprawdź wycinek „cały obiekt”).` };
}

function assessAddedElement(element: string | null, inContent: boolean, part: string): DifferenceAssessment {
  if (element && NOISE_ELEMENTS.has(element)) {
    return { verdict: 'ok', reason: `${element} to znacznik pomocniczy bez wpływu na treść.` };
  }

  if (element && BAKED_FORMATTING.has(element)) {
    return { verdict: 'suspect', reason: `Writer dopisał ${element}, którego Word nie zapisał — formatowanie ze stylu/obliczone zapieczone inline (kontrolowane przybliżenie: wygląd ten sam, plik „cięższy” i mniej edytowalny stylami).` };
  }

  if (!inContent) {
    return { verdict: 'ok', reason: `Dodatkowy element ${element ?? ''} w części ${part} — poza treścią (style/ustawienia/relacje generowane przez writer).` };
  }

  return { verdict: 'suspect', reason: `Writer dodał element ${element ?? ''}, którego nie było w oryginale — sprawdź, czy to celowa normalizacja (np. pusty akapit domykający komórkę), czy artefakt.` };
}

function assessAttributeValue(difference: DocumentDifference, name: string, part: string, inContent: boolean): DifferenceAssessment {
  const left = difference.leftValue ?? '';
  const right = difference.rightValue ?? '';

  if (/(^|\/)_rels\//i.test(part) && name === 'Id') {
    const leftType = /Type="([^"]+)"/.exec(difference.leftExcerpt ?? '')?.[1];
    const rightType = /Type="([^"]+)"/.exec(difference.rightExcerpt ?? '')?.[1];

    if (leftType && rightType && leftType !== rightType) {
      return { verdict: 'ok', reason: 'Artefakt parowania: dwie RÓŻNE relacje sparowane pozycyjnie (inny Type) — prawdziwa różnica to relacja obecna tylko po jednej stronie, patrz osobne wiersze.' };
    }

    return { verdict: 'ok', reason: 'Identyfikator relacji jest dowolny: Word nadaje rId+n, Open XML SDK R+hex — odwołania w treści są przepisane spójnie.' };
  }

  if (NOISE_ATTRIBUTES.test(name)) {
    return { verdict: 'ok', reason: `Atrybut ${name} to szum Worda (sesje, identyfikatory) — wartość nie ma znaczenia.` };
  }

  if (/^(auto)$/i.test(left) && /^(000000|FFFFFF)$/i.test(right)) {
    return { verdict: 'ok', reason: 'Kolor „auto” zapisany jako jawny (ADR-0077) — ten sam wygląd; Word rozwiązuje auto identycznie.' };
  }

  const numericLeft = Number(left);
  const numericRight = Number(right);

  if (UNIT_ATTRIBUTES.test(name) && Number.isFinite(numericLeft) && Number.isFinite(numericRight)) {
    const delta = Math.abs(numericLeft - numericRight);

    if (delta === 0) {
      return { verdict: 'ok', reason: 'Ta sama wartość w innym zapisie liczbowym.' };
    }

    if (delta <= UNIT_TOLERANCE) {
      return { verdict: 'ok', reason: `Różnica ${delta} jednostek to zaokrąglenie px↔twips/EMU po round-tripie (R-13) — niewidoczna.` };
    }

    return { verdict: 'review', reason: `Wartość ${name} zmieniła się o ${delta} jednostek (${left} → ${right}) — więcej niż zaokrąglenie; sprawdź, czy to edycja, czy błąd przeliczenia.` };
  }

  if (name === 'w:val' && /^(w:pStyle|w:rStyle|w:tblStyle)$/.test(elementNameOf(difference) ?? '')) {
    return { verdict: 'bad', reason: `Odwołanie do stylu zmieniło się (${left} → ${right}) — akapit/run dostanie inny styl niż w oryginale.` };
  }

  if (!inContent) {
    return { verdict: 'suspect', reason: `Inna wartość ${name} w części ${part} (${left} → ${right}) — poza treścią; writer zmienił definicję stylu/ustawienie.` };
  }

  return { verdict: 'review', reason: `Inna wartość ${name} (${left} → ${right}) — celowa zmiana formatowania czy błąd writera? Porównaj z wyglądem w edytorze.` };
}

function assessText(difference: DocumentDifference): DifferenceAssessment {
  const left = difference.leftValue ?? '';
  const right = difference.rightValue ?? '';

  if (left.replace(/\s+/g, '') === right.replace(/\s+/g, '')) {
    return { verdict: 'suspect', reason: 'Różnica tylko w białych znakach (spacje/NBSP/tabulatory) — zwykle normalizacja HTML; w Wordzie może zmienić łamanie wiersza.' };
  }

  if (left.trim() === '' || right.trim() === '') {
    return { verdict: 'bad', reason: 'Tekst istnieje tylko po jednej stronie — treść zginęła albo pojawiła się znikąd.' };
  }

  return { verdict: 'review', reason: 'Treść tekstu się różni — jeśli użytkownik nie edytował tego akapitu, to strata/uszkodzenie; jeśli edytował, różnica jest oczekiwana.' };
}
