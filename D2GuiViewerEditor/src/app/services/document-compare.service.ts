import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export type DifferenceKind =
  | 'PartOnlyInLeft'
  | 'PartOnlyInRight'
  | 'BinaryPartChanged'
  | 'ElementOnlyInLeft'
  | 'ElementOnlyInRight'
  | 'ElementNameChanged'
  | 'AttributeOnlyInLeft'
  | 'AttributeOnlyInRight'
  | 'AttributeValueChanged'
  | 'TextChanged'
  | 'ElementMoved';

export type ComparedPartStatus = 'Identical' | 'Changed' | 'OnlyInLeft' | 'OnlyInRight' | 'Unreadable';

export interface DocumentDifference {
  kind: DifferenceKind;
  category: string;
  partPath: string;
  leftPath: string | null;
  rightPath: string | null;
  leftLine: number | null;
  rightLine: number | null;
  name: string | null;
  leftValue: string | null;
  rightValue: string | null;
  leftExcerpt: string | null;
  rightExcerpt: string | null;
  excerptTruncated: boolean;
  leftContext: string | null;
  rightContext: string | null;
}

export interface ComparedPart {
  path: string;
  status: ComparedPartStatus;
  isXml: boolean;
  differenceCount: number;
  leftSize: number | null;
  rightSize: number | null;
  leftElementCount: number | null;
  rightElementCount: number | null;
  note: string | null;
}

export interface ComparedFile {
  fileName: string;
  sizeInBytes: number;
  detectedFormat: string;
  partCount: number;
  notes: string[];
}

export interface DocumentComparisonReport {
  left: ComparedFile;
  right: ComparedFile;
  ignoreRevisionIds: boolean;
  ignoreDocumentProperties: boolean;
  identical: boolean;
  totalDifferences: number;
  truncated: boolean;
  ignoredAttributeCount: number;
  countsByKind: Record<string, number>;
  countsByCategory: Record<string, number>;
  parts: ComparedPart[];
  differences: DocumentDifference[];
  comparedAtUtc: string;
  durationMs: number;
}

export interface CompareOptions {
  ignoreRevisionIds: boolean;
  ignoreDocumentProperties: boolean;
}

@Injectable({ providedIn: 'root' })
export class DocumentCompareService {
  private readonly http = inject(HttpClient);
  private readonly apiUrl = `${environment.apiUrl}/documentcompare`;

  compare(left: File, right: File, options: CompareOptions): Observable<DocumentComparisonReport> {
    const formData = new FormData();
    formData.append('left', left, left.name);
    formData.append('right', right, right.name);
    return this.http.post<DocumentComparisonReport>(`${this.apiUrl}/analyze`, formData, {
      params: {
        ignoreRevisionIds: String(options.ignoreRevisionIds),
        ignoreDocumentProperties: String(options.ignoreDocumentProperties),
      },
    });
  }
}
