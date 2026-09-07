import assert from "node:assert/strict";
import { test } from "node:test";
import { WarmAntigravitySessionCache, type WarmAntigravitySession } from "./warm-session-cache.js";

class FakeSession implements WarmAntigravitySession {
  constructor(public readonly id: string, public warm = true, public busy = false) {}
  suspended = 0;
  isBusy(): boolean { return this.busy; }
  hasWarmProcess(): boolean { return this.warm; }
  suspendAntigravity(): void { this.suspended++; this.warm = false; }
}

test("warm session cache keeps at most three idle CLI processes", () => {
  const cache = new WarmAntigravitySessionCache(3);
  const sessions = ["a", "b", "c", "d"].map((id) => new FakeSession(id));
  for (const session of sessions) cache.register(session);
  cache.touch("a");
  cache.touch("b");
  cache.touch("c");
  cache.touch("d");

  const evicted = cache.trim("d");
  assert.equal(cache.warmCount, 3);
  assert.deepEqual(evicted, ["a"]);
  assert.equal(sessions[0].suspended, 1);
  assert.deepEqual(cache.warmSessionIds.sort(), ["b", "c", "d"]);
});

test("busy and current sessions are never evicted", () => {
  const cache = new WarmAntigravitySessionCache(1);
  const current = new FakeSession("current");
  const busy = new FakeSession("busy", true, true);
  cache.register(current);
  cache.register(busy);

  assert.deepEqual(cache.trim("current"), []);
  assert.equal(cache.warmCount, 2);
  busy.busy = false;
  assert.deepEqual(cache.trim("current"), ["busy"]);
});
