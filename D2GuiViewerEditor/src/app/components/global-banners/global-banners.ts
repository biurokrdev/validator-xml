import { Component, ChangeDetectionStrategy } from '@angular/core';
import { EnvironmentBannerComponent } from '../environment-banner/environment-banner';
import { OfflineBannerComponent } from '../offline-banner/offline-banner';

@Component({
  selector: 'd2-global-banners',
  standalone: true,
  imports: [EnvironmentBannerComponent, OfflineBannerComponent],
  templateUrl: './global-banners.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './global-banners.scss',
})
export class GlobalBannersComponent {}
