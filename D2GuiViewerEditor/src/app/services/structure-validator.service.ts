import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export type StructureSeverity = 'None' | 'Info' | 'Warning' | 'Error';

export type StructureRelationshipStatus = 'Resolved' | 'External' | 'TargetMissing' | 'NotDeclared';

export interface StructurePart {
  path: string;
  contentType: string | null;
  uncompressedSize: number;
  compressedSize: number;
  elementCount: number;
}

export interface StructurePackageEntry {
  path: string;
  uncompressedSize: number;
  compressedSize: number;
  contentType: string | null;
  isXml: boolean;
}

export interface StructureIssue {
  code: string;
  severity: Exclude<StructureSeverity, 'None'>;
  title: string;
  description: string;
}

export interface StructureInspectionSummary {
  inspectionId: string;
  fileName: string;
  fileSizeInBytes: number;
  mainDocumentPartPath: string;
  expiresAtUtc: string;
  elementCount: number;
  errorCount: number;
  warningCount: number;
  infoCount: number;
  schemaIssueCount: number;
  packageIssueCount: number;
  sectionCount: number;
  elementsTruncated: boolean;
  parts: StructurePart[];
  categories: string[];
}

export interface StructureElement {
  id: string;
  parentId: string | null;
  depth: number;
  partPath: string;
  xmlName: string;
  category: string;
  displayName: string;
  preview: string | null;
  searchText: string;
  severity: StructureSeverity;
  issueCount: number;
  hasChildren: boolean;
}

export interface StructureAttribute {
  name: string;
  localName: string;
  namespaceUri: string;
  rawValue: string;
  interpretedValue: string | null;
}

export interface StructureProperty {
  name: string;
  value: string | null;
  source: string;
  sourceReference: string | null;
  isRedundant: boolean;
}

export interface StructureRelationship {
  sourcePart: string;
  relationshipPartPath: string;
  id: string;
  type: string;
  target: string;
  targetMode: string;
  resolvedTarget: string | null;
  status: StructureRelationshipStatus;
}

export interface EditorCompatibilityInfo {
  feature: string;
  level: 'Supported' | 'Partial' | 'Unsupported' | 'Unknown' | string;
  notes: string | null;
}

export interface StructureElementDetails {
  id: string;
  parentId: string | null;
  depth: number;
  partPath: string;
  displayPath: string;
  xmlName: string;
  localName: string;
  namespaceUri: string;
  category: string;
  displayName: string;
  preview: string | null;
  attributes: StructureAttribute[];
  properties: StructureProperty[];
  relationships: StructureRelationship[];
  issues: StructureIssue[];
  editorCompatibility: EditorCompatibilityInfo[];
}

export interface StructureElementXml {
  elementId: string;
  partPath: string;
  displayPath: string;
  xml: string;
  sourceLine: number | null;
}

export interface StructurePartXml {
  partPath: string;
  xml: string;
  highlightElementId: string | null;
  highlightLine: number | null;
}

export interface SchemaIssue {
  code: string;
  severity: string;
  description: string;
  partPath: string | null;
  nodeName: string | null;
  path: string | null;
  elementId: string | null;
  targetVersion: string;
}

export interface SchemaIssues {
  targetVersion: string;
  totalCount: number;
  issues: SchemaIssue[];
}

export interface PackageDiagnostics {
  mainDocumentPartPath: string;
  issues: StructureIssue[];
  entries: StructurePackageEntry[];
  supportedSchemaTargets: string[];
}

export interface HeaderFooterBinding {
  kind: 'Header' | 'Footer';
  type: 'Default' | 'First' | 'Even';
  source: 'Direct' | 'Inherited' | 'Missing';
  sourceSectionNumber: number | null;
  isActive: boolean;
  referenceElementId: string | null;
  relationshipId: string | null;
  relationshipType: string | null;
  targetMode: string | null;
  target: string | null;
  partPath: string | null;
  partRootElementId: string | null;
  partExists: boolean;
  issues: StructureIssue[];
}

export interface DocumentSection {
  number: number;
  sectionPropertiesElementId: string;
  displayPath: string;
  firstPageDifferent: boolean;
  evenAndOddHeaders: boolean;
  headerFooterBindings: HeaderFooterBinding[];
  issues: StructureIssue[];
}

@Injectable({ providedIn: 'root' })
export class StructureValidatorService {
  private readonly http = inject(HttpClient);
  private readonly apiUrl = `${environment.apiUrl}/documentstructure`;

  analyze(file: File): Observable<StructureInspectionSummary> {
    const formData = new FormData();
    formData.append('file', file, file.name);
    return this.http.post<StructureInspectionSummary>(`${this.apiUrl}/analyze`, formData);
  }

  getElements(inspectionId: string): Observable<StructureElement[]> {
    return this.http.get<StructureElement[]>(`${this.apiUrl}/${inspectionId}/elements`);
  }

  getElementDetails(inspectionId: string, elementId: string): Observable<StructureElementDetails> {
    return this.http.get<StructureElementDetails>(
      `${this.apiUrl}/${inspectionId}/elements/${encodeURIComponent(elementId)}`,
    );
  }

  getElementXml(inspectionId: string, elementId: string): Observable<StructureElementXml> {
    return this.http.get<StructureElementXml>(
      `${this.apiUrl}/${inspectionId}/elements/${encodeURIComponent(elementId)}/xml`,
    );
  }

  getPartXml(
    inspectionId: string,
    partPath: string,
    highlightElementId?: string,
  ): Observable<StructurePartXml> {
    const params: Record<string, string> = { path: partPath };
    if (highlightElementId) {
      params['highlightElementId'] = highlightElementId;
    }
    return this.http.get<StructurePartXml>(`${this.apiUrl}/${inspectionId}/parts/xml`, { params });
  }

  getSchemaIssues(inspectionId: string, targetVersion?: string): Observable<SchemaIssues> {
    const params = targetVersion ? { targetVersion } : undefined;
    return this.http.get<SchemaIssues>(`${this.apiUrl}/${inspectionId}/schema-issues`, { params });
  }

  getPackageDiagnostics(inspectionId: string): Observable<PackageDiagnostics> {
    return this.http.get<PackageDiagnostics>(`${this.apiUrl}/${inspectionId}/package-diagnostics`);
  }

  getSections(inspectionId: string): Observable<DocumentSection[]> {
    return this.http.get<DocumentSection[]>(`${this.apiUrl}/${inspectionId}/sections`);
  }

  deleteInspection(inspectionId: string): Observable<void> {
    return this.http.delete<void>(`${this.apiUrl}/${inspectionId}`);
  }
}
