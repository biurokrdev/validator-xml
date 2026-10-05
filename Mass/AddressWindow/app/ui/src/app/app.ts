import { Component } from '@angular/core';
import { LetterCheck } from './features/letter-check/letter-check';

@Component({
  selector: 'app-root',
  imports: [LetterCheck],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {}
