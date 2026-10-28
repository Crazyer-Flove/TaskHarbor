/* Actual UI controller in a DOM test double. No browser, screenshots, or real user data. */
"use strict";
const fs = require("fs"),
  vm = require("vm"),
  assert = require("assert/strict");
const decode = (s) =>
  s
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&amp;/g, "&");
let document;
class Element {
  constructor(tag, attrs = {}) {
    this.tagName = tag.toUpperCase();
    this.attrs = attrs;
    this.children = [];
    this.parentElement = null;
    this.style = {};
    this.listeners = {};
    this._value = attrs.value || "";
    this._text = "";
    this.disabled = false;
    this.open = false;
    this.dataset = new Proxy(
      {},
      {
        get: (_, k) =>
          this.attrs[
            "data-" + String(k).replace(/[A-Z]/g, (x) => "-" + x.toLowerCase())
          ],
      },
    );
  }
  get id() {
    return this.attrs.id || "";
  }
  get className() {
    return this.attrs.class || "";
  }
  set className(v) {
    this.attrs.class = v;
  }
  get value() {
    return this._value;
  }
  set value(v) {
    this._value = String(v);
  }
  get checked() {
    return this._checked === undefined
      ? this.attrs.checked !== undefined
      : this._checked;
  }
  set checked(v) {
    this._checked = !!v;
  }
  get hidden() {
    return this.attrs.hidden !== undefined;
  }
  set hidden(v) {
    if (v) this.attrs.hidden = "";
    else delete this.attrs.hidden;
  }
  get classList() {
    return {
      contains: (c) => this.className.split(/\s+/).includes(c),
      add: (c) => {
        if (!this.classList.contains(c)) this.className += " " + c;
      },
      remove: (c) => {
        this.className = this.className
          .split(/\s+/)
          .filter((x) => x !== c)
          .join(" ");
      },
      toggle: (c, on) => {
        if (on === undefined) on = !this.classList.contains(c);
        if (on) this.classList.add(c);
        else this.classList.remove(c);
      },
    };
  }
  getAttribute(k) {
    return this.attrs[k] ?? null;
  }
  setAttribute(k, v) {
    this.attrs[k] = String(v);
  }
  appendChild(el) {
    el.parentElement = this;
    this.children.push(el);
    return el;
  }
  get innerHTML() {
    return this._html || "";
  }
  set innerHTML(html) {
    this._html = html;
    this.children = [];
    parse(html, this);
  }
  get textContent() {
    return this._text + this.children.map((x) => x.textContent).join("");
  }
  set textContent(v) {
    this._text = String(v);
    this.children = [];
  }
  matches(sel) {
    return sel.split(",").some((x) => {
      x = x.trim();
      if (x[0] === "#") return this.id === x.slice(1);
      if (x[0] === ".") return this.classList.contains(x.slice(1));
      if (x[0] === "[") {
        const m = x.match(/^\[([^=\]]+)(?:="([^"]*)")?\]$/);
        return (
          m &&
          this.attrs[m[1]] != null &&
          (m[2] == null || this.attrs[m[1]] === m[2])
        );
      }
      return this.tagName.toLowerCase() === x;
    });
  }
  closest(sel) {
    return this.matches(sel)
      ? this
      : this.parentElement && this.parentElement.closest(sel);
  }
  querySelectorAll(sel) {
    return this.children.flatMap((c) => [
      ...(c.matches(sel) ? [c] : []),
      ...c.querySelectorAll(sel),
    ]);
  }
  querySelector(sel) {
    return this.querySelectorAll(sel)[0] || null;
  }
  contains(el) {
    return el === this || this.children.some((c) => c.contains(el));
  }
  addEventListener(type, fn) {
    (this.listeners[type] ||= []).push(fn);
  }
  focus() {
    document.activeElement = this;
  }
  click() {
    if (this.onclick)
      return this.onclick({ target: this, preventDefault() {} });
  }
  getClientRects() {
    let e = this;
    while (e) {
      if (e.hidden) return [];
      e = e.parentElement;
    }
    return [{}];
  }
  showModal() {
    this.open = true;
  }
  close() {
    this.open = false;
  }
  requestSubmit() {
    return (
      this.onsubmit && this.onsubmit({ preventDefault() {}, target: this })
    );
  }
}
function parse(html, root) {
  const stack = [root],
    re = /<!--[\s\S]*?-->|<\/?([\w-]+)\b([^>]*)>|([^<]+)/g;
  let m;
  while ((m = re.exec(html))) {
    if (m[0].startsWith("<!--")) continue;
    if (m[3]) {
      stack[stack.length - 1]._text += decode(m[3]);
      continue;
    }
    const tag = m[1].toLowerCase();
    if (m[0][1] === "/") {
      if (stack.length > 1) stack.pop();
      continue;
    }
    const attrs = {};
    m[2].replace(/([^\s=\/]+)(?:="([^"]*)"|='([^']*)')?/g, (_, k, a, b) => {
      attrs[k] = decode(a ?? b ?? "");
      return "";
    });
    const parent = stack[stack.length - 1],
      el = parent.appendChild(new Element(tag, attrs));
    if (
      tag === "option" &&
      (attrs.selected != null || parent.children.length === 1)
    )
      parent.value = attrs.value;
    if (
      ![
        "meta",
        "link",
        "input",
        "img",
        "br",
        "hr",
        "path",
        "circle",
        "rect",
      ].includes(tag) &&
      !m[0].endsWith("/>")
    )
      stack.push(el);
  }
}
document = new Element("document");
parse(
  fs
    .readFileSync("web/panel.html", "utf8")
    .replace(/<script\b[^>]*>[\s\S]*?<\/script>/g, ""),
  document,
);
document.body = document.querySelector("body");
document.activeElement = document.body;
document.getElementById = (id) => document.querySelector("#" + id);
document.createElement = (tag) => new Element(tag);
const date = new Date(),
  today = `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
let tasks = ["q1", "q2", "q3", "q4"].map((q, i) => ({
  id: "task-" + i,
  title: "Initial " + i,
  quadrant: q,
  period: "day",
  planDate: today,
  date: "",
  repeat: "none",
  subtasks: [],
}));
let config = { scope: "all", pinned: true, newPeriod: "day" },
  listener,
  failSave = false,
  failLoad = false,
  commands = [];
const sandbox = {
  document,
  console,
  Date,
  Math,
  Promise,
  FileReader: function () {},
  Image: function () {},
  setTimeout: (fn, ms) => {
    const t = setTimeout(fn, ms);
    t.unref();
    return t;
  },
  clearTimeout,
  setInterval() {},
  addEventListener() {},
  chrome: {
    webview: {
      addEventListener: (_, fn) => {
        listener = fn;
      },
      postMessage(msg) {
        commands.push(msg);
        let r = { id: msg.id, ok: true };
        if (msg.cmd === "load") {
          r = failLoad
            ? { id: msg.id, ok: false, err: "corrupt fixture" }
            : {
                ...r,
                tasks: JSON.stringify(tasks),
                cfg: JSON.stringify(config),
                version: "test",
                demo: false,
              };
        }
        if (msg.cmd === "saveTasks") {
          if (failSave) {
            r.ok = false;
            r.err = "write denied";
            failSave = false;
          } else tasks = JSON.parse(msg.tasks);
        }
        if (msg.cmd === "saveCfg") config = JSON.parse(msg.cfg);
        listener({ data: r });
      },
    },
  },
};
sandbox.window = sandbox;
vm.createContext(sandbox);
vm.runInContext(fs.readFileSync("web/model.js", "utf8"), sandbox);
vm.runInContext(fs.readFileSync("web/panel.js", "utf8"), sandbox);
const $ = (id) => document.getElementById(id),
  tick = () => new Promise(setImmediate),
  event = (target) => ({ target, preventDefault() {} });
let count = 0;
async function test(name, fn) {
  await fn();
  await tick();
  count++;
  console.log("PASS " + name);
}
async function submit() {
  await $("captureForm").onsubmit(event($("captureForm")));
  await tick();
}
async function act(id, action) {
  const card = $("workspace")
    .querySelectorAll("[data-id]")
    .find((x) => x.dataset.id === id);
  assert(card, "missing task " + id);
  const b = card.querySelector(`[data-action="${action}"]`);
  await $("workspace").onclick(event(b));
  await tick();
}
(async () => {
  await tick();
  const S = sandbox.TaskHarborApp.state;
  await test("all quadrants render with actual controller", () => {
    assert(S.ready);
    assert.equal($("matrix").querySelectorAll(".quadrant").length, 4);
  });
  await test("quick capture persists week plan without hard deadline", async () => {
    $("newPeriod").onclick(
      event($("newPeriod").querySelector('[data-period="week"]')),
    );
    $("newTitle").value = "Weekly capture";
    await submit();
    assert.equal(tasks.at(-1).period, "week");
    assert.equal(tasks.at(-1).date, "");
    assert.equal($("newTitle").value, "");
  });
  await test("failed save rolls back task state and keeps input", async () => {
    const n = tasks.length;
    failSave = true;
    $("newTitle").value = "Protected draft";
    await submit();
    assert.equal(tasks.length, n);
    assert.equal(S.tasks.length, n);
    assert.equal($("newTitle").value, "Protected draft");
    assert.equal($("saveError").hidden, false);
  });
  await test("retry commits once and clears saved draft", async () => {
    const n = tasks.length;
    await $("saveRetry").onclick();
    assert.equal(tasks.length, n + 1);
    assert.equal(tasks.filter((t) => t.title === "Protected draft").length, 1);
    assert.equal($("newTitle").value, "");
    assert.equal($("saveError").hidden, true);
  });
  const added = tasks.at(-1).id;
  await test("drawer edits preserve context and add subtasks", async () => {
    await act(added, "edit");
    assert.equal($("editor").hidden, false);
    $("editTitle").value = "Edited task";
    $("editPeriod").value = "day";
    $("editPlanDate").value = today;
    $("subtaskInput").value = "One step";
    $("subtaskAdd").onclick();
    $("editRepeat").value = "day";
    await $("editForm").onsubmit(event($("editForm")));
    assert.equal(tasks.find((t) => t.id === added).subtasks.length, 1);
    assert.equal(tasks.find((t) => t.id === added).repeat, "day");
    assert.equal($("editor").hidden, true);
  });
  await test("failed drawer save can be retried without stale unsaved state", async () => {
    await act(added, "edit");
    $("editTitle").value = "Retry edit";
    failSave = true;
    await $("editForm").onsubmit(event($("editForm")));
    assert.equal($("editor").hidden, false);
    assert.equal(tasks.find((t) => t.id === added).title, "Edited task");
    await $("saveRetry").onclick();
    assert.equal(tasks.find((t) => t.id === added).title, "Retry edit");
    assert.equal($("editor").hidden, true);
  });
  await test("completion creates next repeating occurrence", async () => {
    const n = tasks.length;
    await act(added, "complete");
    assert.equal(tasks.length, n + 1);
    assert(tasks.find((t) => t.id === added).done);
    assert(tasks.some((t) => t.generatedFrom === added && !t.done));
  });
  await test("undo reverts completion and generated occurrence together", async () => {
    await $("undo").onclick();
    assert.equal(tasks.find((t) => t.id === added).done, false);
    assert(!tasks.some((t) => t.generatedFrom === added));
  });
  await test("deletion is recoverable in recycle bin", async () => {
    await act(added, "edit");
    $("editDelete").onclick();
    await tick();
    assert(tasks.find((t) => t.id === added).deletedAt);
    $("trashOpen").onclick();
    const restore = $("trashList").querySelector(
      '[data-restore="' + added + '"]',
    );
    assert(restore);
    await $("trashList").onclick(event(restore));
    assert(!tasks.find((t) => t.id === added).deletedAt);
    $("trashClose").onclick();
  });
  await test("search excludes nonmatching tasks without changing storage", () => {
    const n = tasks.length;
    $("search").value = "Retry edit";
    $("search").oninput();
    assert.equal($("matrix").querySelectorAll(".task").length, 1);
    assert.equal(tasks.length, n);
    $("search").value = "";
    $("search").oninput();
  });
  await test("batch complete persists selected tasks", async () => {
    $("bulkToggle").onclick();
    await act("task-0", "select");
    await act("task-1", "select");
    const b = $("bulkBar").querySelector('[data-bulk="complete"]');
    await $("bulkBar").onclick(event(b));
    assert(tasks.find((t) => t.id === "task-0").done);
    assert(tasks.find((t) => t.id === "task-1").done);
    $("bulkCancel").onclick();
  });
  await test("corrupt load blocks writes instead of replacing old data", async () => {
    failLoad = true;
    await sandbox.TaskHarborApp.reload();
    assert.equal(S.ready, false);
    const n = commands.filter((x) => x.cmd === "saveTasks").length;
    $("newTitle").value = "Must not overwrite";
    await submit();
    assert.equal(commands.filter((x) => x.cmd === "saveTasks").length, n);
    assert.equal($("loadFailure").hidden, false);
    failLoad = false;
    await sandbox.TaskHarborApp.reload();
  });
  await test("bridge request IDs remain numeric for sound commands", () => {
    assert(
      commands
        .filter((x) => x.cmd === "sound")
        .every(
          (x) => typeof x.id === "number" && typeof x.soundId === "string",
        ),
    );
  });
  console.log(
    "UI controller checks passed: " +
      count +
      " (DOM simulation; no visual claim).",
  );
})().catch((e) => {
  console.error(e);
  process.exitCode = 1;
});
