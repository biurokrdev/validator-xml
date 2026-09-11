import { describe, it, expect, beforeAll } from 'vitest';
import { installPromiseTry } from './promise-try';

describe('installPromiseTry (Promise.try na ZoneAwarePromise)', () => {
  beforeAll(() => installPromiseTry());

  it('globalny Promise (po zone.js) ma Promise.try', () => {
    expect(typeof (Promise as any).try).toBe('function');
  });

  it('nie nadpisuje istniejącej natywnej implementacji', () => {
    const native = () => 'native';
    const fake = { try: native } as unknown as PromiseConstructor;
    installPromiseTry(fake);
    expect((fake as any).try).toBe(native);
  });

  it('woła fn synchronicznie z argumentami i rozstrzyga wynikiem', async () => {
    let calledSync = false;
    const p = (Promise as any).try((a: number, b: number) => { calledSync = true; return a + b; }, 2, 3);
    expect(calledSync).toBe(true);
    expect(p).toBeInstanceOf(Promise);
    await expect(p).resolves.toBe(5);
  });

  it('wyjątek synchroniczny z fn = odrzucona obietnica (nie throw)', async () => {
    const err = new Error('boom');
    const p = (Promise as any).try(() => { throw err; });
    await expect(p).rejects.toBe(err);
  });

  it('spłaszcza obietnicę zwróconą z fn (rozstrzygnięcie i odrzucenie)', async () => {
    await expect((Promise as any).try(() => Promise.resolve('ok'))).resolves.toBe('ok');
    await expect((Promise as any).try(() => Promise.reject(new Error('x')))).rejects.toThrow('x');
  });

  it('instalowany na kontrolowanym konstruktorze — używa `this` jako C', async () => {
    let constructed = 0;
    function Fake(this: unknown, executor: (res: (v: unknown) => void, rej: (e: unknown) => void) => void) {
      constructed++;
      return new Promise(executor);
    }
    const ctor = Fake as unknown as PromiseConstructor;
    expect(typeof (ctor as any).try).toBe('undefined');
    installPromiseTry(ctor);
    await expect((ctor as any).try(() => 1)).resolves.toBe(1);
    expect(constructed).toBe(1);
  });
});
