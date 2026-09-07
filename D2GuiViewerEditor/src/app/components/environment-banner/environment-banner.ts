import { Component, computed, inject, ChangeDetectionStrategy } from '@angular/core';
import { BuildInfoService } from '../../core/services/build-info.service';
import { resolveEnvironmentBanner } from '../../core/utils/environment-banner.util';

@Component({
  selector: 'd2-environment-banner',
  standalone: true,
  templateUrl: './environment-banner.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './environment-banner.scss',
})
export class EnvironmentBannerComponent {
  private readonly buildInfo = inject(BuildInfoService);

  protected readonly view = computed(() =>
    resolveEnvironmentBanner(this.buildInfo.environment()),
  );
}
