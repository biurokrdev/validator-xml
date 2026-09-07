import { Component, ChangeDetectionStrategy } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'd2-admin-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './admin-shell.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './admin-shell.scss'
})
export class AdminShellComponent {}
