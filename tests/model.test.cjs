"use strict";
process.env.TZ = "Asia/Shanghai";
const fs = require("fs");
const vm = require("vm");
const assert = require("assert/strict");
const sandbox = { module: { exports: {} }, Date, Math, console };
vm.runInNewContext(fs.readFileSync("web/model.js", "utf8"), sandbox);
const M = sandbox.module.exports;
let passed = 0;
function test(name, fn) {
  fn();
  passed++;
  console.log("PASS " + name);
}
const now = new Date("2026-10-27T12:00:00+08:00");
function task(extra) {
  return M.normalize(
    [
      Object.assign(
        {
          id: "one",
          title: "Example task",
          quadrant: "q2",
          period: "day",
          planDate: "2026-10-27",
        },
        extra,
      ),
    ],
    now,
  )[0];
}
test("legacy migration keeps deadline and unknown metadata", () => {
  const raw = {
    id: "legacy",
    title: "Legacy",
    date: "2026-10-31",
    due: "18:00",
    pri: "high",
    custom: 42,
  };
  const t = M.normalize([raw], now)[0];
  assert.equal(t.date, raw.date);
  assert.equal(t.due, raw.due);
  assert.equal(t.quadrant, "q1");
  assert.equal(t.custom, 42);
  assert.equal(raw.schemaVersion, undefined);
});
test("legacy low priority maps to q4", () =>
  assert.equal(task({ quadrant: null, pri: "low" }).quadrant, "q4"));
test("all quadrants survive reload", () =>
  M.quadrants.forEach((q) => assert.equal(task({ quadrant: q }).quadrant, q)));
test("missing IDs and duplicate IDs are repaired", () => {
  const t = M.normalize(
    [{ title: "A", id: "x" }, { title: "B", id: "x" }, { title: "C" }],
    now,
  );
  assert.equal(new Set(t.map((x) => x.id)).size, 3);
});
test("invalid top level data rejected", () =>
  assert.throws(() => M.normalize({}, now)));
test("invalid task rows rejected", () =>
  assert.throws(() => M.normalize([null], now)));
test("week plan has no invented deadline", () => {
  const t = task({ period: "week" });
  assert.equal(t.date, "");
  assert.equal(t.planDate, "2026-10-26");
  assert.equal(M.late(t, now), false);
});
test("month plan has no invented deadline", () => {
  const t = task({ period: "month" });
  assert.equal(t.date, "");
  assert.equal(t.planDate, "2026-10-01");
});
test("week correctly crosses new year", () =>
  assert.equal(
    M.range("week", "2026-12-31").join(","),
    "2026-12-28,2027-01-03",
  ));
test("leap month ends on February 29", () =>
  assert.equal(M.range("month", "2028-02-01")[1], "2028-02-29"));
test("invalid calendar dates rejected", () => {
  assert.equal(M.parse("2026-02-29"), null);
  assert.equal(M.parse("2026-13-01"), null);
  assert.equal(M.parse("bad"), null);
});
test("normal task validates", () =>
  assert.equal(M.validate(task()).title, "Example task"));
test("empty title rejected", () =>
  assert.throws(() => M.validate(task({ title: " " }))));
test("deadline time requires date", () =>
  assert.throws(() => M.validate(task({ due: "15:00" }))));
test("invalid deadline time rejected", () =>
  assert.throws(() => M.validate(task({ date: "2026-10-27", due: "25:00" }))));
test("reminder requires explicit deadline", () =>
  assert.throws(() => M.validate(task({ remind: true }))));
test("oversized task title rejected", () =>
  assert.throws(() => M.validate(task({ title: "x".repeat(501) }))));
test("too many subtasks rejected", () =>
  assert.throws(() =>
    M.validate(
      task({ subtasks: Array.from({ length: 51 }, () => ({ title: "x" })) }),
    ),
  ));
test("week view includes contained day plans", () =>
  assert(M.inScope(task(), "week", now, false)));
test("day view does not automatically include monthly plans", () =>
  assert.equal(M.inScope(task({ period: "month" }), "day", now, false), false));
test("month view includes week crossing month boundary", () =>
  assert(
    M.inScope(
      task({ period: "week", planDate: "2026-09-28" }),
      "month",
      new Date("2026-10-01T12:00:00+08:00"),
      false,
    ),
  ));
test("overdue task optionally appears in today", () => {
  const t = task({
    period: "week",
    planDate: "2026-09-21",
    date: "2026-09-25",
  });
  assert(M.inScope(t, "day", now, true));
  assert.equal(M.inScope(t, "day", now, false), false);
});
test("date-only deadline stays valid through its whole local day", () =>
  assert.equal(
    M.late(task({ date: "2026-10-27" }), new Date("2026-10-27T23:59:59+08:00")),
    false,
  ));
test("time-specific deadline becomes overdue", () =>
  assert(M.late(task({ date: "2026-10-27", due: "09:00" }), now)));
test("completed tasks are not overdue", () =>
  assert.equal(M.late(task({ date: "2020-01-01", done: true }), now), false));
test("deleted tasks excluded from scope and stats", () => {
  const t = task({ deletedAt: now.toISOString(), date: "2020-01-01" });
  assert.equal(M.inScope(t, "all", now, true), false);
  assert.equal(M.stats([t], now).pending, 0);
});
test("daily statistics use local completion time at midnight", () => {
  const t = task({
    done: true,
    doneAt: "2026-10-26T16:30:00Z",
    date: "2026-10-01",
  });
  assert.equal(
    M.stats([t], new Date("2026-10-27T00:45:00+08:00")).doneToday,
    1,
  );
});
test("trend and daily statistics share the same completion date", () => {
  const t = task({ done: true, doneAt: "2026-10-26T16:30:00Z" }),
    st = M.stats([t], now, 7);
  assert.equal(st.trend[6].count, 1);
  assert.equal(st.trend[5].count, 0);
});
test("monthly advance retains month-end anchor", () => {
  assert.equal(M.advance("2026-01-31", "month", 31), "2026-02-28");
  assert.equal(M.advance("2026-02-28", "month", 31), "2026-03-31");
});
test("leap-year monthly advance", () =>
  assert.equal(M.advance("2028-01-31", "month", 31), "2028-02-29"));
test("completion does not mutate original array", () => {
  const t = task();
  M.complete([t], t.id, true, now);
  assert.equal(t.done, false);
});
test("daily recurrence generates exactly one next task", () => {
  const result = M.complete([task({ repeat: "day" })], "one", true, now);
  assert.equal(result.length, 2);
  assert.equal(result[1].planDate, "2026-10-28");
  assert.equal(result[1].date, "");
  assert.equal(result[1].done, false);
});
test("repeating completion does not duplicate next occurrence", () => {
  const first = M.complete([task({ repeat: "week" })], "one", true, now);
  assert.equal(M.complete(first, "one", true, now).length, 2);
});
test("weekly recurrence retains deadline offset", () => {
  const result = M.complete(
    [
      task({
        period: "week",
        repeat: "week",
        date: "2026-10-30",
        due: "18:00",
      }),
    ],
    "one",
    true,
    now,
  );
  assert.equal(result[1].planDate, "2026-11-02");
  assert.equal(result[1].date, "2026-11-06");
  assert.equal(result[1].due, "18:00");
});
test("monthly recurrence preserves 31st through February", () => {
  const source = task({
    period: "month",
    planDate: "2026-01-01",
    date: "2026-01-31",
    repeat: "month",
  });
  const first = M.complete(
    [source],
    "one",
    true,
    new Date("2026-01-31T12:00:00+08:00"),
  );
  const second = M.complete(
    first,
    first[1].id,
    true,
    new Date("2026-02-28T12:00:00+08:00"),
  );
  assert.equal(second[2].date, "2026-03-31");
});
test("recurrence resets subtask completion", () => {
  const result = M.complete(
    [
      task({
        repeat: "day",
        subtasks: [{ id: "s", title: "Step", done: true }],
      }),
    ],
    "one",
    true,
    now,
  );
  assert.equal(result[1].subtasks[0].done, false);
  assert.notEqual(result[1].subtasks[0].id, "s");
});
test("late repeating completion skips missed periods without flooding", () => {
  const result = M.complete(
    [task({ planDate: "2025-01-01", repeat: "day" })],
    "one",
    true,
    now,
  );
  assert.equal(result.length, 2);
  assert(result[1].planDate >= "2026-10-27");
});
test("restoring incomplete preserves saved deadline", () => {
  const result = M.complete(
    [task({ done: true, date: "2026-10-30" })],
    "one",
    false,
    now,
  );
  assert.equal(result[0].doneAt, null);
  assert.equal(result[0].date, "2026-10-30");
});
test("HTML IDs and referenced elements exist", () => {
  const html = fs.readFileSync("web/panel.html", "utf8"),
    js = fs.readFileSync("web/panel.js", "utf8");
  const ids = [...html.matchAll(/\bid="([^"]+)"/g)].map((x) => x[1]);
  assert.equal(ids.length, new Set(ids).size);
  const references = [...js.matchAll(/\$\(["']([^"']+)["']\)/g)];
  assert(references.length > 100, "UI reference scan must actually inspect the controller");
  const missing = references
    .map((x) => x[1])
    .filter((x) => !ids.includes(x));
  assert.deepEqual([...new Set(missing)], []);
});
for (const file of ["web/panel.js", "web/model.js"])
  new vm.Script(fs.readFileSync(file, "utf8"), { filename: file });
console.log("Domain/static checks passed: " + passed);
