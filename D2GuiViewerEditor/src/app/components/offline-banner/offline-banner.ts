import { Component, inject, ChangeDetectionStrategy } from '@angular/core';
import { ConnectionStatusService } from '../../core/services/connection-status.service';
import { BuildInfoService } from '../../core/services/build-info.service';

@Component({
  selector: 'd2-offline-banner',
  standalone: true,
  templateUrl: './offline-banner.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './offline-banner.scss'
})
export class OfflineBannerComponent {
  readonly connectionStatus = inject(ConnectionStatusService);
  readonly buildInfo = inject(BuildInfoService);
}
