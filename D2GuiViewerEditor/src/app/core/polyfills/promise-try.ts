export function installPromiseTry(ctor: PromiseConstructor = Promise): void {
  if (typeof (ctor as { try?: unknown }).try === 'function') {
    return;
  }
  Object.defineProperty(ctor, 'try', {
    configurable: true,
    writable: true,
    enumerable: false,
    value: function promiseTry<T, A extends unknown[]>(
      this: PromiseConstructor,
      fn: (...args: A) => T | PromiseLike<T>,
      ...args: A
    ): Promise<Awaited<T>> {
      const C = typeof this === 'function' ? this : ctor;
      return new C<Awaited<T>>((resolve) => {
        resolve(fn(...args) as Awaited<T>);
      });
    },
  });
}

installPromiseTry();
