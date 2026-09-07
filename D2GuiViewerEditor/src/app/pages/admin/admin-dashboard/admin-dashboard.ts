import { Component, inject, OnInit, signal, ChangeDetectionStrategy } from '@angular/core';
import { AdminService, AdminStats } from '../../../services/admin.service';

@Component({
  selector: 'd2-admin-dashboard',
  standalone: true,
  imports: [],
  templateUrl: './admin-dashboard.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './admin-dashboard.scss'
})
export class AdminDashboardComponent implements OnInit {
  private adminService = inject(AdminService);

  stats = signal<AdminStats | null>(null);
  isLoading = signal(true);
  error = signal<string | null>(null);

  ngOnInit(): void {
    this.adminService.getAdminStats().subscribe({
      next: (s) => {
        this.stats.set(s);
        this.isLoading.set(false);
      },
      error: () => {
        this.error.set('Nie udało się załadować statystyk.');
        this.isLoading.set(false);
      }
    });
  }

  formatBytes(bytes: number): string {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return `${parseFloat((bytes / Math.pow(k, i)).toFixed(1))} ${sizes[i]}`;
  }

  formatDate(dateStr: string): string {
    return new Date(dateStr).toLocaleDateString('pl-PL', {
      day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit'
    });
  }
}
