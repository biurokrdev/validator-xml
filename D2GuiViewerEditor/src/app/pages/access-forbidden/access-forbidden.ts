import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'd2-access-forbidden',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './access-forbidden.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './access-forbidden.scss',
})
export class AccessForbiddenComponent {}
