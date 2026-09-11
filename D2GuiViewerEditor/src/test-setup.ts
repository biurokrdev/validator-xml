import 'zone.js';
import 'zone.js/testing';
import { installPromiseTry } from './app/core/polyfills/promise-try';

installPromiseTry();

if (typeof (document as any).queryCommandState !== 'function') {
  (document as any).queryCommandState = () => false;
}
