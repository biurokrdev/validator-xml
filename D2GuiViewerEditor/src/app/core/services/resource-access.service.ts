import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of, map, tap } from 'rxjs';
import { environment } from '../../../environments/environment';
import { MSAL_CUSTOM_CONFIG, isAuthDisabled } from '../config/runtime-config';

@Injectable({ providedIn: 'root' })
export class ResourceAccessService {
  private readonly http = inject(HttpClient);
  private readonly authConfig = inject(MSAL_CUSTOM_CONFIG);
  private cached?: string[];

  getResources(): Observable<string[]> {
    if (this.cached) {
      return of(this.cached);
    }
    return this.http
      .get<string[]>(`${environment.apiUrl}/identity/resources`)
      .pipe(tap((resources) => (this.cached = resources)));
  }

  hasAccessToResource(name: string): Observable<boolean> {
    if (isAuthDisabled(this.authConfig)) {
      return of(true);
    }
    return this.getResources().pipe(map((resources) => resources.includes(name)));
  }
}
