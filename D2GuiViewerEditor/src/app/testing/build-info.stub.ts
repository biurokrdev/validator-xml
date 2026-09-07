import { signal } from '@angular/core';

export const buildInfoStub = () => ({
  environment: signal('DEV'),
  buildNumber: signal('0.0.0-test'),
  buildDate: signal('2026-01-01'),
  isApiData: signal(false),
});
