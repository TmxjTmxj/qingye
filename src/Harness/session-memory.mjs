import { MemorySessionPersistence } from './memory-storage.mjs';

export const name = 'qingye-session-memory';
export function apply(ctx) {
  new MemorySessionPersistence(ctx);
}
