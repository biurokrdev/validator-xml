import { Routes } from '@angular/router';
import { DashboardComponent } from './pages/dashboard/dashboard';
import { DocumentEditorComponent } from './components/document-editor/document-editor';
import { PdfMaintenanceComponent } from './pages/pdf-maintenance/pdf-maintenance';
import { documentAccessGuard } from './guards/document-access.guard';
import { resourceGuard } from './guards/resource.guard';
import { authGuard } from './guards/auth.guard';
import { homeRedirectGuard } from './guards/home-redirect.guard';

export const routes: Routes = [
  { path: '', component: DashboardComponent, canActivate: [authGuard, homeRedirectGuard] },
  {
    path: 'editor', component: DocumentEditorComponent, canActivate: [authGuard, resourceGuard, documentAccessGuard] },
  {
    path: 'viewer',
    canActivate: [authGuard, resourceGuard, documentAccessGuard],
    loadComponent: () =>
      import('./pages/pdf-viewer/pdf-viewer').then((m) => m.PdfViewerComponent),
  },
  {
    path: 'brak-uprawnien',
    loadComponent: () =>
      import('./pages/access-forbidden/access-forbidden').then((m) => m.AccessForbiddenComponent),
  },
  { path: 'access-denied', redirectTo: 'brak-uprawnien', pathMatch: 'full' },
  {
    path: 'pdf-maintenance',
    component: PdfMaintenanceComponent,
    canActivate: [authGuard, resourceGuard],
    data: { resource: 'viewer' },
  },
  {
    path: 'admin',
    canActivate: [authGuard, resourceGuard],
    loadComponent: () =>
      import('./pages/admin/admin-shell/admin-shell').then((m) => m.AdminShellComponent),
    children: [
      { path: '', redirectTo: 'files', pathMatch: 'full' },
      {
        path: 'files',
        loadComponent: () =>
          import('./pages/admin/admin-files/admin-files').then((m) => m.AdminFilesComponent),
      },
      {
        path: 'deliveries',
        loadComponent: () =>
          import('./pages/admin/admin-deliveries/admin-deliveries').then((m) => m.AdminDeliveriesComponent),
      },
      {
        path: 'walidator-struktury',
        loadComponent: () =>
          import('./pages/admin/admin-structure-validator/admin-structure-validator').then(
            (m) => m.AdminStructureValidatorComponent,
          ),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
