import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'd2-pdf-maintenance',
  standalone: true,
  imports: [RouterLink],
  template: `
    <div class="maintenance-wrapper">
      <div class="maintenance-content">
        <div class="maintenance-icon">
          <svg viewBox="0 0 24 24" width="64" height="64" fill="currentColor">
            <path d="M20 2H8c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm-8.5 7.5c0 .83-.67 1.5-1.5 1.5H9v2H7.5V7H10c.83 0 1.5.67 1.5 1.5v1zm5 2c0 .83-.67 1.5-1.5 1.5h-2.5V7H15c.83 0 1.5.67 1.5 1.5v3zm4-3H19v1h1.5V11H19v2h-1.5V7h3v1.5zM9 9.5h1v-1H9v1zM4 6H2v14c0 1.1.9 2 2 2h14v-2H4V6zm10 5.5h1v-3h-1v3z"/>
          </svg>
        </div>
        <p class="maintenance-message">Trwają prace serwisowe, podgląd PDF chwilowo niedostępny</p>
        <a class="back-link" routerLink="/">← Wróć do strony głównej</a>
      </div>
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.Eager,
  styles: [`
    .maintenance-wrapper {
      display: flex;
      align-items: center;
      justify-content: center;
      min-height: 100%;
      height: 100%;
      background: #f5f6fa;
      padding: 24px;
    }

    .maintenance-content {
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 20px;
      text-align: center;
    }

    .maintenance-icon {
      color: #ff6200;
      opacity: 0.7;
    }

    .maintenance-message {
      font-size: 1.4rem;
      font-weight: 500;
      color: #333;
      margin: 0;
      max-width: 480px;
      line-height: 1.5;
    }

    .back-link {
      color: #ff6200;
      text-decoration: none;
      font-size: 0.95rem;
      font-weight: 500;

      &:hover {
        text-decoration: underline;
      }
    }
  `]
})
export class PdfMaintenanceComponent {}
