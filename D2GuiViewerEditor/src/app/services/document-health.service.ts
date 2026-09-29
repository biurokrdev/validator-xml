import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export type HealthVerdict = 'Healthy' | 'Warnings' | 'NeedsRepair' | 'Corrupt';
export type WordOpenVerdict = 'Ok' | 'Repair' | 'CannotOpen';
export type PdfConversionVerdict = 'Ok' | 'AtRisk' | 'Likely' | 'Blocked';
export type HealthStage = 'File' | 'Package' | 'Xml' | 'Structure' | 'Conversion' | 'Application';
export type HealthSeverity = 'Info' | 'Warning' | 'Error';
export type WordOpenImpact = 'None' | 'Repair' | 'CannotOpen';
export type PdfConversionImpact = 'None' | 'Possible' | 'Likely' | 'Blocking';
export type ProbeStatus = 'Passed' | 'Warning' | 'Failed' | 'Skipped';
/** Poziom obsługi konstrukcji przez komponent naszego pipeline'u (reader / edytor / writer). */
export type AppSupportLevel = 'Unknown' | 'Full' | 'Partial' | 'PassThrough' | 'Unsupported';
export type RoundTripOutcome = 'NotVerified' | 'Preserved' | 'Reduced' | 'Lost';
export type CoverageStatus = 'Supported' | 'Partial' | 'PassThrough' | 'Unsupported' | 'UnexpectedLoss' | 'Unverified';

export interface HealthFinding {
  code: string;
  severity: HealthSeverity;
  stage: HealthStage;
  title: string;
  description: string;
  location: string | null;
  wordImpact: WordOpenImpact;
  pdfImpact: PdfConversionImpact;
  remedy: string | null;
  /** Jak z tym problemem radzi sobie NASZA aplikacja (reader/edytor/writer). */
  appSupport: AppSupportLevel;
  appNote: string | null;
}

/**
 * Konstrukcja DOCX obecna w dokumencie zestawiona z obsługą przez nasz pipeline: zadeklarowane
 * poziomy per komponent (rejestr zweryfikowany w kodzie), zmierzony wynik round-tripu
 * DOCX→HTML→DOCX i wskaźnik do miejsca w kodzie.
 */
export interface ImplementationCoverageItem {
  featureKey: string;
  label: string;
  sourceCount: number;
  sampleLocation: string | null;
  reader: AppSupportLevel;
  editor: AppSupportLevel;
  writer: AppSupportLevel;
  roundTripCount: number | null;
  roundTrip: RoundTripOutcome;
  status: CoverageStatus;
  note: string;
  codePointer: string | null;
}

export interface ConversionProbe {
  id: string;
  name: string;
  description: string;
  status: ProbeStatus;
  durationMs: number;
  message: string | null;
  details: string | null;
}

export interface DocumentHealthStatistics {
  packageEntries: number;
  xmlParts: number;
  imageParts: number;
  imageBytes: number;
  elements: number;
  paragraphs: number;
  tables: number;
  maxTableNesting: number;
  drawings: number;
  fields: number;
  sections: number;
  footnotes: number;
  endnotes: number;
  comments: number;
  trackedRevisions: number;
  contentControls: number;
  altChunks: number;
  embeddedFonts: number;
}

export interface DocumentHealthReport {
  fileName: string;
  fileSizeInBytes: number;
  detectedFormat: string;
  mainDocumentPartPath: string | null;
  verdict: HealthVerdict;
  verdictSummary: string;
  wordOpen: WordOpenVerdict;
  wordOpenSummary: string;
  pdfConversion: PdfConversionVerdict;
  pdfConversionSummary: string;
  errorCount: number;
  warningCount: number;
  infoCount: number;
  findingsTruncated: boolean;
  findings: HealthFinding[];
  probes: ConversionProbe[];
  statistics: DocumentHealthStatistics;
  /** Pokrycie konstrukcji dokumentu przez naszą implementację — tylko konstrukcje obecne w pliku. */
  coverage: ImplementationCoverageItem[];
  coverageSummary: string;
  analyzedAtUtc: string;
  durationMs: number;
}

/**
 * Klient API narzędzia administracyjnego „Kondycja dokumentu". Jedno wywołanie zwraca pełny raport —
 * bez stanu po stronie serwera, więc raport można zapisać albo wkleić do zgłoszenia.
 */
@Injectable({ providedIn: 'root' })
export class DocumentHealthService {
  private readonly http = inject(HttpClient);
  private readonly apiUrl = `${environment.apiUrl}/documenthealth`;

  analyze(file: File, includeConversionProbes: boolean): Observable<DocumentHealthReport> {
    const formData = new FormData();
    formData.append('file', file, file.name);
    return this.http.post<DocumentHealthReport>(`${this.apiUrl}/analyze`, formData, {
      params: { includeConversionProbes: String(includeConversionProbes) },
    });
  }
}
