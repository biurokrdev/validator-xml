import 'zone.js';
import 'zone.js/testing';

if (typeof (document as any).queryCommandState !== 'function') {
  (document as any).queryCommandState = () => false;
}
