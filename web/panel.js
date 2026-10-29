/* TaskHarbor UI + WebView2 bridge. MIT. */
(function () {
  "use strict";
  var M = window.TaskHarborModel,
    $ = function (id) {
      return document.getElementById(id);
    };
  var WV = window.chrome && window.chrome.webview,
    sequence = 0,
    pending = new Map();
  var S = {
    tasks: [],
    cfg: {},
    scope: "day",
    query: "",
    list: false,
    bulk: false,
    selected: new Set(),
    saving: false,
    ready: false,
    editor: null,
    editorSubtasks: [],
    editorOriginal: "",
    undo: null,
    retry: null,
    aiItems: [],
    images: [],
    aiBusy: false,
    trendDays: 7,
    dragId: "",
    demo: !WV,
  };
  var Q = [
    ["q1", "重要紧急", "立即处理"],
    ["q2", "重要不紧急", "计划安排"],
    ["q3", "不重要紧急", "快速处理"],
    ["q4", "不重要不紧急", "有空再做"],
  ];
  var P = { day: "日计划", week: "周计划", month: "月计划" };
  var previewCfg = {},
    previewTasks = [];
  function esc(s) {
    return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) {
      return {
        "&": "&amp;",
        "<": "&lt;",
        ">": "&gt;",
        '"': "&quot;",
        "'": "&#39;",
      }[c];
    });
  }
  function seed() {
    var today = M.day(new Date()),
      yesterday = M.parse(today);
    yesterday.setDate(yesterday.getDate() - 1);
    return M.normalize([
      {
        id: "demo-1",
        title: "提交客户方案",
        quadrant: "q1",
        period: "day",
        planDate: today,
        date: M.day(yesterday),
        due: "18:00",
      },
      {
        id: "demo-2",
        title: "确认上线检查清单",
        quadrant: "q1",
        period: "day",
        planDate: today,
        date: today,
        due: "15:00",
      },
      {
        id: "demo-3",
        title: "修复登录异常",
        quadrant: "q1",
        period: "week",
        planDate: today,
        date: today,
        due: "17:30",
      },
      {
        id: "demo-4",
        title: "准备季度复盘",
        quadrant: "q2",
        period: "week",
        planDate: today,
        subtasks: [
          { title: "收集指标", done: true },
          { title: "整理结论", done: false },
        ],
      },
      {
        id: "demo-5",
        title: "学习产品分析课程",
        quadrant: "q2",
        period: "month",
        planDate: today,
      },
      {
        id: "demo-6",
        title: "回复会议时间确认",
        quadrant: "q3",
        period: "day",
        planDate: today,
        date: today,
        due: "16:00",
      },
      {
        id: "demo-7",
        title: "提交报销单据",
        quadrant: "q3",
        period: "week",
        planDate: today,
      },
      {
        id: "demo-8",
        title: "整理收藏的参考资料",
        quadrant: "q4",
        period: "month",
        planDate: today,
      },
      {
        id: "demo-9",
        title: "完成本周工作记录",
        quadrant: "q2",
        period: "day",
        planDate: today,
        done: true,
        doneAt: new Date().toISOString(),
      },
    ]);
  }
  async function mock(cmd, data) {
    if (cmd === "load") {
      try {
        previewTasks =
          JSON.parse(localStorage.getItem("taskharbor.demo.tasks") || "null") ||
          seed();
        previewCfg = JSON.parse(
          localStorage.getItem("taskharbor.demo.cfg") || "{}",
        );
      } catch (_) {
        previewTasks = seed();
      }
      return {
        tasks: JSON.stringify(previewTasks),
        cfg: JSON.stringify(previewCfg),
        version: "3.0.3 preview",
        dataPath: "浏览器演示数据（与桌面数据隔离）",
        demo: true,
        hotkey: false,
      };
    }
    if (cmd === "saveTasks") {
      previewTasks = JSON.parse(data.tasks);
      localStorage.setItem("taskharbor.demo.tasks", data.tasks);
      return { ok: true };
    }
    if (cmd === "saveCfg") {
      previewCfg = JSON.parse(data.cfg);
      var safe = M.clone(previewCfg);
      delete safe.apiKey;
      delete safe.imgApiKey;
      localStorage.setItem("taskharbor.demo.cfg", JSON.stringify(safe));
      return { ok: true };
    }
    if (cmd === "setLayout") {
      applyLayout(data.layout);
      return { ok: true };
    }
    if (cmd === "collapse") {
      applyMode("dock");
      return { ok: true };
    }
    if (cmd === "expand" || cmd === "ballDown") {
      applyMode("panel");
      return { ok: true };
    }
    if (
      [
        "alive",
        "setInteracting",
        "setPinned",
        "setBadge",
        "sound",
        "applyCfg",
        "setTopmost",
        "setAlpha",
        "drag",
      ].includes(cmd)
    )
      return { ok: true };
    if (cmd === "aiRun")
      return { ok: false, err: "浏览器演示不发送 AI 请求，请使用桌面版。" };
    return { ok: false, err: "此操作需要 TaskHarbor 桌面版" };
  }
  function call(cmd, data, timeout) {
    if (!WV) return mock(cmd, data || {});
    return new Promise(function (resolve, reject) {
      var id = ++sequence,
        timer = setTimeout(function () {
          pending.delete(id);
          reject(new Error("操作超时，请重试"));
        }, timeout || 15000);
      pending.set(id, { resolve: resolve, reject: reject, timer: timer });
      WV.postMessage(Object.assign({ cmd: cmd, id: id }, data || {}));
    });
  }
  function fire(cmd, data) {
    call(cmd, data).catch(function () {});
  }
  if (WV)
    WV.addEventListener("message", function (e) {
      var d = e.data;
      if (!d) return;
      if (d.evt) {
        event(d);
        return;
      }
      var p = pending.get(d.id);
      if (p) {
        clearTimeout(p.timer);
        pending.delete(d.id);
        p.resolve(d);
      }
    });
  function toast(text, undo, action) {
    $("toastText").textContent = text;
    $("undo").hidden = !undo;
    $("toastAction").hidden = !action;
    $("toastAction").onclick = action || null;
    $("toast").hidden = false;
    clearTimeout(toast.timer);
    toast.timer = setTimeout(
      function () {
        if (!$("toast").contains(document.activeElement))
          $("toast").hidden = true;
      },
      undo ? 9000 : 5000,
    );
  }
  function status(text, failed) {
    $("saveStatus").textContent = text;
    $("saveStatus").className = failed ? "failed" : "";
  }
  function persistCfg() {
    return call("saveCfg", { cfg: JSON.stringify(S.cfg) }).then(function (r) {
      if (!r.ok) throw new Error(r.err || "设置未能保存");
      return r;
    });
  }
  async function save(next, label, options) {
    options = options || {};
    if (!S.ready) {
      toast("任务尚未载入，不能修改数据");
      return false;
    }
    if (S.saving) {
      toast("正在保存，请稍候");
      return false;
    }
    var before = M.clone(S.tasks);
    S.saving = true;
    S.retry = null;
    $("saveError").hidden = true;
    S.tasks = M.clone(next);
    status("保存中…");
    busy(true);
    render();
    try {
      var r = await call("saveTasks", { tasks: JSON.stringify(next) });
      if (!r.ok) throw new Error(r.err || "无法写入任务文件");
      S.tasks = M.clone(next);
      if (options.undo !== false) S.undo = before;
      status(S.demo ? "演示数据已保存" : "已保存到本机");
      if (options.capture && $("newTitle").value.trim() === options.capture) {
        $("newTitle").value = "";
        $("newDate").value = "";
        $("newTime").value = "";
      }
      if (options.afterCommit) await options.afterCommit();
      if (label) toast(label, options.undo !== false);
      if (S.cfg.soundId !== "none" && options.sound !== false)
        fire("sound", { soundId: options.complete ? "rise" : "soft" });
      return true;
    } catch (e) {
      S.tasks = before;
      S.retry = { next: M.clone(next), label: label, options: options };
      status("未保存 · 修改已还原", true);
      $("saveErrorText").textContent =
        "保存失败，修改已还原，录入内容保留。" + e.message;
      $("saveError").hidden = false;
      return false;
    } finally {
      S.saving = false;
      busy(false);
      render();
    }
  }
  function busy(on) {
    $("app").setAttribute("aria-busy", on);
    ["addTask", "editSave", "aiCommit", "saveRetry"].forEach(function (id) {
      $(id).disabled = on;
    });
  }
  function scoped() {
    return S.tasks.filter(function (t) {
      return (
        M.inScope(t, S.scope, new Date(), S.cfg.includeOverdue !== false) &&
        (!S.query || t.title.toLowerCase().includes(S.query))
      );
    });
  }
  function dateText(t) {
    if (!t.date) return "无截止时间";
    var d =
      t.date === M.day(new Date()) ? "今天" : t.date.slice(5).replace("-", "/");
    return d + (t.due ? " " + t.due : " 截止");
  }
  function taskHtml(t) {
    var isLate = M.late(t),
      doneChildren = t.subtasks.filter(function (s) {
        return s.done;
      }).length;
    return (
      '<article class="task ' +
      (t.done ? "done " : "") +
      (S.editor === t.id ? "is-selected" : "") +
      '" data-id="' +
      esc(t.id) +
      '" draggable="' +
      (!t.done && !S.bulk) +
      '">' +
      (S.bulk && !t.done
        ? '<label class="complete-control"><input data-action="select" type="checkbox" aria-label="选择 ' +
          esc(t.title) +
          '" ' +
          (S.selected.has(t.id) ? "checked" : "") +
          "></label>"
        : '<label class="complete-control" title="' +
          (t.done ? "恢复未完成" : "完成任务") +
          '"><input type="checkbox" data-action="complete" aria-label="' +
          (t.done ? "恢复未完成：" : "完成：") +
          esc(t.title) +
          '" ' +
          (t.done ? "checked" : "") +
          "></label>") +
      '<div class="task-body"><button class="task-title" data-action="edit" title="' +
      esc(t.title) +
      '">' +
      esc(t.title) +
      '</button><div class="task-meta"><span class="' +
      (isLate ? "late" : "") +
      '">' +
      esc(dateText(t)) +
      (isLate ? " · 逾期" : "") +
      '</span><span class="tag">' +
      P[t.period] +
      "</span>" +
      (t.subtasks.length
        ? "<span>" + doneChildren + "/" + t.subtasks.length + " 子任务</span>"
        : "") +
      (t.repeat !== "none" ? '<span class="tag">重复</span>' : "") +
      (t.ai ? '<span class="tag">AI</span>' : "") +
      '</div></div><button class="task-more" data-action="edit" aria-label="编辑 ' +
      esc(t.title) +
      '">编辑</button></article>'
    );
  }
  function render() {
    if (!S.ready) return;
    var list = scoped(),
      open = list.filter(function (t) {
        return !t.done;
      }),
      done = list.filter(function (t) {
        return t.done;
      });
    open.sort(function (a, b) {
      return (
        Number(M.late(b)) - Number(M.late(a)) ||
        ((a.date || "9999-12-31") + (a.due || "23:59")).localeCompare(
          (b.date || "9999-12-31") + (b.due || "23:59"),
        )
      );
    });
    var r = M.range(S.scope, M.day(new Date()));
    $("rangeLabel").textContent =
      S.scope === "all"
        ? "全部计划"
        : { day: "今天", week: "本周", month: "本月" }[S.scope] +
          " · " +
          r[0].slice(5).replace("-", "/") +
          (r[0] === r[1] ? "" : "–" + r[1].slice(5).replace("-", "/"));
    $("scopeCounts").innerHTML =
      "待处理 " +
      open.length +
      ' · <span class="late-count">逾期 ' +
      open.filter(function (t) {
        return M.late(t);
      }).length +
      "</span>";
    document.querySelectorAll("[data-scope]").forEach(function (b) {
      b.setAttribute("aria-pressed", b.dataset.scope === S.scope);
    });
    document.querySelectorAll("[data-period]").forEach(function (b) {
      b.setAttribute(
        "aria-pressed",
        b.dataset.period === (S.cfg.newPeriod || "day"),
      );
    });
    $("matrix").innerHTML = Q.map(function (q) {
      var items = open.filter(function (t) {
        return t.quadrant === q[0];
      });
      return (
        '<section class="quadrant ' +
        q[0] +
        '" data-quadrant="' +
        q[0] +
        '" aria-label="' +
        q[1] +
        '"><header class="q-head"><h2>' +
        q[1] +
        '</h2><span class="count">' +
        items.length +
        '</span><span class="q-action">' +
        q[2] +
        '</span><button data-add="' +
        q[0] +
        '" aria-label="添加' +
        q[1] +
        '任务">添加</button></header><div class="q-items">' +
        (items.length
          ? items.map(taskHtml).join("")
          : '<div class="q-empty">' +
            (S.query ? "没有匹配任务" : "暂无任务") +
            '<button data-add="' +
            q[0] +
            '">添加一件事</button></div>') +
        "</div></section>"
      );
    }).join("");
    $("completedSummary").textContent = "已完成 " + done.length;
    $("completedList").innerHTML =
      done
        .sort(function (a, b) {
          return String(b.doneAt).localeCompare(String(a.doneAt));
        })
        .map(taskHtml)
        .join("") || '<p class="help">完成的任务会出现在这里</p>';
    $("workspace").classList.toggle("list-view", S.list);
    $("viewToggle").textContent = S.list ? "四象限" : "列表";
    $("viewToggle").setAttribute("aria-pressed", S.list);
    $("bulkToggle").setAttribute("aria-pressed", S.bulk);
    $("bulkBar").hidden = !S.bulk;
    $("selectionCount").textContent = "已选 " + S.selected.size + " 项";
    var allActive = S.tasks.filter(function (t) {
      return !t.deletedAt;
    });
    $("filterNotice").hidden = !!list.length;
    $("filterNoticeText").textContent = S.query
      ? "没有匹配的任务，试试清除搜索"
      : allActive.length
        ? "当前计划范围没有任务，可切换到全部"
        : "还没有任务，从下方记下第一件事";
    $("clearFilters").hidden = !allActive.length;
    var st = M.stats(S.tasks, new Date(), S.trendDays);
    $("globalPending").textContent = st.pending;
    $("globalLate").textContent = st.overdue;
    $("todayDone").textContent = st.doneToday;
    var max = Math.max.apply(
      null,
      st.trend
        .map(function (x) {
          return x.count;
        })
        .concat([1]),
    );
    $("trend").classList.toggle("is-month", S.trendDays > 7);
    $("trend").innerHTML = st.trend
      .map(function (x) {
        return (
          '<div class="bar" title="' +
          x.date +
          " 完成 " +
          x.count +
          ' 项"><meter min="0" max="' +
          max +
          '" value="' +
          x.count +
          '" style="--bar-height:' +
          Math.max(3, (40 * x.count) / max) +
          'px" aria-label="' +
          x.date +
          " 完成 " +
          x.count +
          ' 项"></meter><small>' +
          x.date.slice(8) +
          "</small></div>"
        );
      })
      .join("");
    var badge = allActive.filter(function (t) {
      return (
        !t.done &&
        (M.late(t) || (t.period === "day" && t.planDate === M.day(new Date())))
      );
    }).length;
    if (render.badge !== badge) {
      render.badge = badge;
      fire("setBadge", { n: badge });
    }
    if (!$("trash").hidden) renderTrash();
  }
  function applyMode(mode) {
    document.body.dataset.mode = mode;
  }
  function applyLayout(layout) {
    S.cfg.layout = layout === "compact" ? "compact" : "workbench";
    document.body.dataset.layout = S.cfg.layout;
    $("layoutToggle").textContent =
      S.cfg.layout === "compact" ? "工作台" : "小面板";
  }
  function overlayOpen() {
    return (
      ["editor", "settings", "ai", "trash"].some(function (id) {
        return !$(id).hidden;
      }) || $("confirmDialog").open
    );
  }
  function interaction() {
    var el = document.activeElement;
    fire("setInteracting", {
      on:
        overlayOpen() ||
        S.saving ||
        !!$("newTitle").value.trim() ||
        (el === $("search") && !!el.value),
    });
  }
  function showLayer(id) {
    $(id).hidden = false;
    interaction();
    var first = $(id).querySelector("button,input,textarea,select");
    if (first) first.focus();
  }
  function hideLayer(id) {
    $(id).hidden = true;
    interaction();
    $("newTitle").focus();
  }
  function confirmAction(title, text) {
    return new Promise(function (resolve) {
      $("confirmTitle").textContent = title;
      $("confirmText").textContent = text;
      $("confirmDialog").showModal();
      interaction();
      var finish = function (on) {
        $("confirmDialog").close();
        interaction();
        resolve(on);
      };
      $("confirmYes").onclick = function () {
        finish(true);
      };
      $("confirmNo").onclick = function () {
        finish(false);
      };
      $("confirmDialog").oncancel = function (e) {
        e.preventDefault();
        finish(false);
      };
      $("confirmNo").focus();
    });
  }
  function editorData() {
    return {
      title: $("editTitle").value.trim(),
      quadrant: $("editQuadrant").value,
      period: $("editPeriod").value,
      planDate: M.planAnchor($("editPeriod").value, $("editPlanDate").value),
      date: $("editDate").value,
      due: $("editTime").value,
      remind: $("editRemind").checked,
      repeat: $("editRepeat").value,
      subtasks: M.clone(S.editorSubtasks),
    };
  }
  function editorDirty() {
    return S.editor && JSON.stringify(editorData()) !== S.editorOriginal;
  }
  async function closeEditor(force) {
    if (
      !force &&
      editorDirty() &&
      !(await confirmAction(
        "放弃未保存的修改？",
        "此任务尚未保存。选择确定后保留原来的任务内容。",
      ))
    )
      return false;
    $("editor").hidden = true;
    $("drawerScrim").hidden = true;
    document.body.classList.remove("drawer-open");
    S.editor = null;
    interaction();
    render();
    return true;
  }
  async function edit(id) {
    if (S.saving) return;
    if (S.editor && S.editor !== id && !(await closeEditor())) return;
    var t = S.tasks.find(function (x) {
      return x.id === id && !x.deletedAt;
    });
    if (!t) return;
    S.editor = id;
    S.editorSubtasks = M.clone(t.subtasks);
    $("editTitle").value = t.title;
    $("editQuadrant").value = t.quadrant;
    $("editPeriod").value = t.period;
    $("editPlanDate").value = t.planDate;
    $("editDate").value = t.date || "";
    $("editTime").value = t.due || "";
    $("editRemind").checked = !!t.remind;
    $("editRepeat").value = t.repeat;
    $("editError").textContent = "";
    $("snooze").hidden = !t.remind || !t.date;
    renderSubtasks();
    S.editorOriginal = JSON.stringify(editorData());
    $("editor").hidden = false;
    $("drawerScrim").hidden = false;
    document.body.classList.add("drawer-open");
    interaction();
    render();
    $("editTitle").focus();
  }
  function renderSubtasks() {
    $("subtaskCount").textContent =
      S.editorSubtasks.filter(function (s) {
        return s.done;
      }).length +
      "/" +
      S.editorSubtasks.length;
    $("subtaskList").innerHTML = S.editorSubtasks
      .map(function (s, i) {
        return (
          '<div class="subtask-row"><label><input type="checkbox" data-sub="' +
          i +
          '" ' +
          (s.done ? "checked" : "") +
          ">" +
          esc(s.title) +
          '</label><button type="button" data-remove-sub="' +
          i +
          '" aria-label="删除子任务 ' +
          esc(s.title) +
          '">移除</button></div>'
        );
      })
      .join("");
  }
  async function trashIds(ids) {
    if (S.saving) return;
    var next = M.clone(S.tasks);
    next.forEach(function (t) {
      if (ids.includes(t.id)) {
        t.deletedAt = new Date().toISOString();
      }
    });
    await save(next, "已移入回收站", {
      afterCommit: async function () {
        S.selected.clear();
        if (ids.includes(S.editor)) await closeEditor(true);
      },
    });
  }
  function renderTrash() {
    var items = S.tasks.filter(function (t) {
      return !!t.deletedAt;
    });
    $("trashList").innerHTML = items.length
      ? items
          .map(function (t) {
            return (
              '<div class="trash-item"><div>' +
              esc(t.title) +
              "<small>删除于 " +
              esc(new Date(t.deletedAt).toLocaleString()) +
              '</small></div><button data-restore="' +
              esc(t.id) +
              '">恢复</button></div>'
            );
          })
          .join("")
      : '<p class="help">回收站为空</p>';
    $("trashEmpty").disabled = !items.length || S.saving;
  }
  async function addTask(e) {
    e.preventDefault();
    if (S.saving) return;
    var title = $("newTitle").value.trim(),
      period = S.cfg.newPeriod || "day",
      t = {
        id: M.uid(),
        title: title,
        period: period,
        planDate: M.planAnchor(period, M.day(new Date())),
        date: $("newDate").value,
        due: $("newTime").value,
        quadrant: $("newQuadrant").value,
        done: false,
        doneAt: null,
        repeat: "none",
        subtasks: [],
        schemaVersion: 3,
      };
    try {
      M.validate(t);
    } catch (err) {
      toast(err.message);
      $("newTitle").focus();
      return;
    }
    var next = M.clone(S.tasks);
    next.push(t);
    if (await save(next, "已添加任务", { capture: title })) {
      if (
        !M.inScope(t, S.scope, new Date(), S.cfg.includeOverdue !== false) ||
        (S.query && !t.title.toLowerCase().includes(S.query))
      ) {
        toast("已添加，当前筛选未显示此任务", true, function () {
          S.scope = "all";
          S.query = "";
          $("search").value = "";
          render();
        });
      }
      $("newTitle").focus();
      interaction();
    }
  }
  $("captureForm").onsubmit = addTask;
  $("newPeriod").onclick = function (e) {
    var b = e.target.closest("[data-period]");
    if (!b) return;
    S.cfg.newPeriod = b.dataset.period;
    persistCfg().catch(function (err) {
      toast(err.message);
    });
    render();
  };
  $("newQuadrant").onchange = function () {
    S.cfg.newQuadrant = this.value;
    persistCfg().catch(function (err) {
      toast(err.message);
    });
  };
  $("captureMore").onclick = function () {
    $("captureExtra").hidden = !$("captureExtra").hidden;
    this.setAttribute("aria-expanded", !$("captureExtra").hidden);
  };
  $("clearDeadline").onclick = function () {
    $("newDate").value = "";
    $("newTime").value = "";
  };
  $("scopeTabs").onclick = function (e) {
    var b = e.target.closest("[data-scope]");
    if (!b) return;
    S.scope = b.dataset.scope;
    S.cfg.scope = S.scope;
    S.selected.clear();
    persistCfg().catch(function (err) {
      toast(err.message);
    });
    render();
  };
  $("search").oninput = function () {
    S.query = this.value.trim().toLowerCase();
    S.selected.clear();
    render();
    interaction();
  };
  $("clearFilters").onclick = function () {
    S.scope = "all";
    S.query = "";
    $("search").value = "";
    render();
  };
  $("statsToggle").onclick = function () {
    $("stats").hidden = !$("stats").hidden;
    this.setAttribute("aria-expanded", !$("stats").hidden);
    S.cfg.statsExpanded = !$("stats").hidden;
    persistCfg().catch(function (err) {
      toast(err.message);
    });
  };
  $("trendToggle").onclick = function () {
    S.trendDays = S.trendDays === 7 ? 30 : 7;
    this.textContent = "近 " + S.trendDays + " 天";
    render();
  };
  $("viewToggle").onclick = function () {
    S.list = !S.list;
    S.cfg.listView = S.list;
    persistCfg().catch(function (err) {
      toast(err.message);
    });
    render();
  };
  $("layoutToggle").onclick = async function () {
    var layout = S.cfg.layout === "compact" ? "workbench" : "compact";
    var r = await call("setLayout", { layout: layout }).catch(function (e) {
      toast(e.message);
      return {};
    });
    if (r.ok) applyLayout(layout);
    else toast(r.err || "无法切换布局");
  };
  $("pinToggle").onclick = async function () {
    var on = !S.cfg.pinned,
      old = S.cfg.pinned;
    S.cfg.pinned = on;
    try {
      await persistCfg();
      fire("setPinned", { on: on });
      this.setAttribute("aria-pressed", on);
      toast(on ? "面板已固定" : "已恢复自动收起");
    } catch (e) {
      S.cfg.pinned = old;
      toast(e.message);
    }
  };
  $("collapse").onclick = function () {
    fire("collapse");
  };
  $("dockFallback").onmousedown = function (e) {
    if (e.button === 0) fire("ballDown");
  };
  $("bulkToggle").onclick = function () {
    S.bulk = !S.bulk;
    S.selected.clear();
    render();
  };
  $("bulkCancel").onclick = function () {
    S.bulk = false;
    S.selected.clear();
    render();
  };
  $("bulkBar").onclick = async function (e) {
    var b = e.target.closest("[data-bulk]");
    if (!b || S.saving) return;
    var ids = Array.from(S.selected);
    if (!ids.length) {
      toast("请先选择任务");
      return;
    }
    if (b.dataset.bulk === "delete") {
      await trashIds(ids);
      return;
    }
    var next = M.clone(S.tasks);
    ids.forEach(function (id) {
      if (b.dataset.bulk === "complete") next = M.complete(next, id, true);
      else {
        var t = next.find(function (x) {
          return x.id === id;
        });
        if (t) {
          t.period = "day";
          t.planDate = M.day(new Date());
        }
      }
    });
    if (
      await save(next, "已更新 " + ids.length + " 项任务", {
        complete: b.dataset.bulk === "complete",
      })
    ) {
      S.selected.clear();
      render();
    }
  };
  $("workspace").onclick = async function (e) {
    var add = e.target.closest("[data-add]");
    if (add) {
      $("newQuadrant").value = add.dataset.add;
      $("newTitle").focus();
      return;
    }
    var b = e.target.closest("[data-action]"),
      card = e.target.closest("[data-id]");
    if (!b || !card || S.saving) return;
    var id = card.dataset.id,
      t = S.tasks.find(function (x) {
        return x.id === id;
      });
    if (!t) return;
    if (b.dataset.action === "select") {
      if (S.selected.has(id)) S.selected.delete(id);
      else S.selected.add(id);
      render();
      return;
    }
    if (b.dataset.action === "complete") {
      await save(
        M.complete(S.tasks, id, !t.done),
        t.done ? "已恢复未完成" : "已完成任务",
        { complete: !t.done },
      );
      return;
    }
    if (b.dataset.action === "edit") edit(id);
  };
  $("matrix").ondragstart = function (e) {
    var card = e.target.closest("[data-id]");
    if (!card || S.saving || S.bulk) {
      e.preventDefault();
      return;
    }
    S.dragId = card.dataset.id;
    e.dataTransfer.setData("text/plain", S.dragId);
    e.dataTransfer.effectAllowed = "move";
    card.classList.add("dragging");
    fire("setInteracting", { on: true });
  };
  $("matrix").ondragover = function (e) {
    var q = e.target.closest("[data-quadrant]");
    if (!q || !S.dragId) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = "move";
    document.querySelectorAll(".drop-target").forEach(function (el) {
      el.classList.remove("drop-target");
    });
    q.classList.add("drop-target");
  };
  $("matrix").ondrop = async function (e) {
    var q = e.target.closest("[data-quadrant]");
    if (!q || !S.dragId) return;
    e.preventDefault();
    var id = S.dragId;
    S.dragId = "";
    var next = M.clone(S.tasks),
      t = next.find(function (x) {
        return x.id === id;
      });
    if (t) {
      t.quadrant = q.dataset.quadrant;
      await save(next, "已调整象限");
    }
    interaction();
  };
  document.addEventListener("dragend", function () {
    S.dragId = "";
    document.querySelectorAll(".dragging,.drop-target").forEach(function (el) {
      el.classList.remove("dragging");
      el.classList.remove("drop-target");
    });
    interaction();
  });
  $("editForm").onsubmit = async function (e) {
    e.preventDefault();
    if (S.saving) return;
    var data = editorData(),
      next = M.clone(S.tasks),
      t = next.find(function (x) {
        return x.id === S.editor;
      });
    if (!t) return;
    try {
      M.validate(data);
    } catch (err) {
      $("editError").textContent = err.message;
      return;
    }
    if (data.date !== t.date || data.due !== t.due) {
      delete t.snoozeUntil;
    }
    if (
      data.repeat !== t.repeat ||
      data.date !== t.date ||
      data.planDate !== t.planDate
    ) {
      delete t.repeatAnchorDay;
      delete t.repeatPlanDay;
    }
    Object.assign(t, data);
    await save(next, "修改已保存", {
      afterCommit: function () {
        return closeEditor(true);
      },
    });
  };
  $("editClose").onclick = $("editCancel").onclick = function () {
    closeEditor();
  };
  $("drawerScrim").onclick = function () {
    closeEditor();
  };
  $("editDelete").onclick = function () {
    if (S.editor) trashIds([S.editor]);
  };
  $("editPeriod").onchange = function () {
    $("editPlanDate").value = M.planAnchor(this.value, $("editPlanDate").value);
  };
  $("subtaskAdd").onclick = function () {
    var title = $("subtaskInput").value.trim();
    if (!title) return;
    if (S.editorSubtasks.length >= 50) {
      toast("最多 50 个子任务");
      return;
    }
    S.editorSubtasks.push({ id: M.uid(), title: title, done: false });
    $("subtaskInput").value = "";
    renderSubtasks();
  };
  $("subtaskInput").onkeydown = function (e) {
    if (e.key === "Enter") {
      e.preventDefault();
      $("subtaskAdd").click();
    }
  };
  $("subtaskList").onchange = function (e) {
    if (e.target.dataset.sub != null) {
      S.editorSubtasks[+e.target.dataset.sub].done = e.target.checked;
      renderSubtasks();
    }
  };
  $("subtaskList").onclick = function (e) {
    var b = e.target.closest("[data-remove-sub]");
    if (b) {
      S.editorSubtasks.splice(+b.dataset.removeSub, 1);
      renderSubtasks();
    }
  };
  $("snooze").onclick = async function () {
    if (editorDirty()) {
      toast("请先保存修改，再延后提醒");
      return;
    }
    var next = M.clone(S.tasks),
      t = next.find(function (x) {
        return x.id === S.editor;
      });
    if (!t) return;
    t.snoozeUntil = new Date(Date.now() + 15 * 60000).toISOString();
    t.remind = true;
    if (await save(next, "已设为 15 分钟后提醒")) await closeEditor(true);
  };
  $("undo").onclick = async function () {
    if (!S.undo || S.saving) return;
    var before = S.undo;
    if (await save(before, "已撤销上一步", { undo: false })) {
      S.undo = null;
      $("undo").hidden = true;
    }
  };
  $("saveRetry").onclick = async function () {
    if (S.retry && !S.saving) {
      var retry = S.retry;
      await save(retry.next, retry.label, retry.options);
    }
  };
  $("toastClose").onclick = function () {
    $("toast").hidden = true;
  };
  $("trashList").onclick = async function (e) {
    var b = e.target.closest("[data-restore]");
    if (!b) return;
    var next = M.clone(S.tasks),
      t = next.find(function (x) {
        return x.id === b.dataset.restore;
      });
    if (t) {
      delete t.deletedAt;
      await save(next, "任务已恢复");
    }
  };
  $("trashEmpty").onclick = async function () {
    if (
      await confirmAction(
        "永久清空回收站？",
        "此操作无法撤销。建议先导出备份。",
      )
    ) {
      if (
        await save(
          S.tasks.filter(function (t) {
            return !t.deletedAt;
          }),
          "回收站已清空",
          { undo: false },
        )
      ) {
        S.undo = null;
      }
    }
  };
  $("trashOpen").onclick = function () {
    renderTrash();
    showLayer("trash");
  };
  $("trashClose").onclick = function () {
    hideLayer("trash");
  };

  var providers = {
    deepseek: ["https://api.deepseek.com/v1", "deepseek-chat"],
    qwen: ["https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"],
    openai: ["https://api.openai.com/v1", "gpt-4o-mini"],
    ollama: ["http://localhost:11434/v1", "qwen2.5:7b"],
    custom: ["", ""],
  };
  function fillSettings() {
    var c = S.cfg;
    $("settingIdle").value = String(c.idleDelay == null ? 1000 : c.idleDelay);
    $("settingTop").checked = c.topmost !== false;
    $("settingAuto").checked = !!c.autoStart;
    $("settingReminders").checked = c.remindersEnabled !== false;
    $("settingQuiet").checked = c.quietHours !== false;
    $("settingOverdue").checked = c.includeOverdue !== false;
    $("settingSound").value = c.soundId || "soft";
    $("settingAlpha").value = Math.round(((+c.ballAlpha || 255) * 100) / 255);
    $("settingProvider").value = c.provider || "deepseek";
    $("settingUrl").value = c.baseUrl || "";
    $("settingModel").value = c.model || "";
    $("settingKey").value = c.apiKey || "";
    $("settingEndpoint").value = c.endpoint || "/chat/completions";
    $("settingImageSeparate").checked = !!c.imgSeparate;
    $("settingImageUrl").value = c.imgBaseUrl || "";
    $("settingImageModel").value = c.imgModel || "";
    $("settingImageKey").value = c.imgApiKey || "";
    $("settingCustomPrompt").checked = !!c.customPrompt;
    $("settingPrompt").value = c.prompt || "";
    $("dataPath").textContent = c.dataPath || "";
    $("version").textContent = "TaskHarbor " + (c.version || "3.0.3");
    $("hotkeyStatus").textContent = c.hotkey
      ? "全局快捷键 Ctrl + Shift + T"
      : "全局快捷键 Ctrl + Shift + T 暂不可用，可从托盘打开";
    $("migrationNote").textContent =
      c.migrationNote ||
      "旧版 DeskTodo 数据首次启动时自动复制迁移，原文件保留。";
    $("settingsError").textContent = "";
  }
  $("settingsOpen").onclick = async function () {
    if (S.editor && !(await closeEditor())) return;
    fillSettings();
    showLayer("settings");
  };
  $("settingProvider").onchange = function () {
    var p = providers[this.value];
    if (p) {
      $("settingUrl").value = p[0];
      $("settingModel").value = p[1];
    }
  };
  $("settingsClose").onclick = async function () {
    var old = M.clone(S.cfg),
      c = M.clone(S.cfg);
    Object.assign(c, {
      idleDelay: +$("settingIdle").value,
      topmost: $("settingTop").checked,
      remindersEnabled: $("settingReminders").checked,
      quietHours: $("settingQuiet").checked,
      includeOverdue: $("settingOverdue").checked,
      soundId: $("settingSound").value,
      glass: true,
      ballAlpha: Math.round((+$("settingAlpha").value * 255) / 100),
      provider: $("settingProvider").value,
      baseUrl: $("settingUrl").value.trim(),
      model: $("settingModel").value.trim(),
      apiKey: $("settingKey").value.trim(),
      endpoint: $("settingEndpoint").value.trim() || "/chat/completions",
      imgSeparate: $("settingImageSeparate").checked,
      imgBaseUrl: $("settingImageUrl").value.trim(),
      imgModel: $("settingImageModel").value.trim(),
      imgApiKey: $("settingImageKey").value.trim(),
      customPrompt: $("settingCustomPrompt").checked,
      prompt: $("settingPrompt").value,
    });
    try {
      if (c.baseUrl && !/^https?:\/\//i.test(c.baseUrl))
        throw new Error("AI 接口地址需以 http:// 或 https:// 开头");
      if (c.imgSeparate && c.imgBaseUrl && !/^https?:\/\//i.test(c.imgBaseUrl))
        throw new Error("图片接口地址无效");
      S.cfg = c;
      await persistCfg();
      var auto = $("settingAuto").checked;
      if (auto !== !!old.autoStart) {
        var a = await call("setAutoStart", { on: auto });
        if (!a.ok) throw new Error(a.err || "启动项更新失败");
        S.cfg.autoStart = !!a.on;
      }
      var result = await call("applyCfg");
      if (!result.ok) throw new Error(result.err || "设置应用失败");
      hideLayer("settings");
      render();
      toast("设置已保存");
    } catch (e) {
      S.cfg = old;
      $("settingsError").textContent = e.message;
    }
  };
  async function hostAction(cmd, success) {
    try {
      var r = await call(cmd);
      if (r.err || r.ok === false) throw new Error(r.err || "操作未完成");
      if (r.path || r.ok) toast(success || "操作完成");
      return r;
    } catch (e) {
      toast(e.message);
      return null;
    }
  }
  $("exportData").onclick = function () {
    hostAction("exportBackup", "任务备份已导出");
  };
  $("importData").onclick = async function () {
    if (
      !(await confirmAction(
        "导入任务备份？",
        "将替换当前任务，宿主会先保存一份导入前备份。",
      ))
    )
      return;
    var r = await hostAction("importBackup", "备份已导入");
    if (r && r.ok) {
      await load();
      fillSettings();
    }
  };
  $("openData").onclick = function () {
    hostAction("openDataDir");
  };
  $("changeData").onclick = async function () {
    var r = await hostAction("pickFolder", "存储位置已更新");
    if (r && r.path) {
      S.cfg.dataPath = r.path;
      $("dataPath").textContent = r.path;
    }
  };
  $("createShortcut").onclick = function () {
    hostAction("makeShortcut", "桌面快捷方式已创建");
  };
  $("quit").onclick = async function () {
    if (
      await confirmAction(
        "退出 TaskHarbor？",
        "退出后悬浮入口与任务提醒停止，已保存的任务会保留。",
      )
    )
      fire("quit");
  };
  function aiState(mode) {
    $("aiInput").hidden = mode !== "input";
    $("aiRunning").hidden = mode !== "running";
    $("aiPreview").hidden = mode !== "preview";
  }
  $("aiOpen").onclick = async function () {
    if (S.editor && !(await closeEditor())) return;
    $("aiDestination").textContent = S.cfg.baseUrl
      ? "将发送到 " + S.cfg.baseUrl + " · " + (S.cfg.model || "未设置模型")
      : "尚未配置 AI 服务，请先在设置中填写接口与模型。";
    showLayer("ai");
    aiState("input");
    $("aiText").focus();
  };
  $("aiClose").onclick = async function () {
    if (
      S.aiBusy &&
      !(await confirmAction(
        "取消本次 AI 整理？",
        "已识别但尚未加入的任务不会保存。",
      ))
    )
      return;
    if (S.aiBusy) {
      fire("aiCancel");
      S.aiBusy = false;
    }
    hideLayer("ai");
  };
  function readImage(file) {
    return new Promise(function (resolve, reject) {
      if (!/^image\//.test(file.type)) {
        reject(new Error("请选择图片文件"));
        return;
      }
      if (file.size > 15 * 1024 * 1024) {
        reject(new Error("单张图片最多 15MB"));
        return;
      }
      var reader = new FileReader();
      reader.onerror = function () {
        reject(new Error("图片读取失败"));
      };
      reader.onload = function () {
        var im = new Image();
        im.onerror = function () {
          reject(new Error("无法解码这张图片"));
        };
        im.onload = function () {
          var scale = Math.min(1, 1280 / Math.max(im.width, im.height)),
            cv = document.createElement("canvas");
          cv.width = Math.max(1, Math.round(im.width * scale));
          cv.height = Math.max(1, Math.round(im.height * scale));
          cv.getContext("2d").drawImage(im, 0, 0, cv.width, cv.height);
          resolve(cv.toDataURL("image/jpeg", 0.82));
        };
        im.src = reader.result;
      };
      reader.readAsDataURL(file);
    });
  }
  async function addImages(files) {
    for (var file of Array.from(files)) {
      if (S.images.length >= 3) {
        toast("最多 3 张图片");
        break;
      }
      try {
        S.images.push(await readImage(file));
      } catch (e) {
        toast(e.message);
      }
    }
    $("aiImages").innerHTML = S.images
      .map(function (data, i) {
        return (
          '<div class="thumbnail"><img alt="待识别图片 ' +
          (i + 1) +
          '" src="' +
          data +
          '"><button data-remove-image="' +
          i +
          '">移除</button></div>'
        );
      })
      .join("");
  }
  $("aiFiles").onchange = function () {
    addImages(this.files);
    this.value = "";
  };
  $("aiImages").onclick = function (e) {
    var b = e.target.closest("[data-remove-image]");
    if (b) {
      S.images.splice(+b.dataset.removeImage, 1);
      addImages([]);
    }
  };
  document.addEventListener("paste", function (e) {
    if ($("ai").hidden || !e.clipboardData) return;
    var files = Array.from(e.clipboardData.items)
      .filter(function (x) {
        return x.type.startsWith("image/");
      })
      .map(function (x) {
        return x.getAsFile();
      });
    if (files.length) {
      e.preventDefault();
      addImages(files);
    }
  });
  $("aiGenerate").onclick = async function () {
    var text = $("aiText").value.trim();
    $("aiError").textContent = "";
    if (!text && !S.images.length) {
      $("aiError").textContent = "请先输入文字或选择图片";
      return;
    }
    if (!S.cfg.baseUrl || !S.cfg.model) {
      $("aiError").textContent = "请在设置中配置服务地址和模型";
      return;
    }
    S.aiBusy = true;
    S.aiItems = [];
    aiState("running");
    $("aiStream").textContent = "";
    $("aiProgress").textContent = "正在整理…";
    try {
      var r = await call("aiRun", { text: text, images: S.images });
      if (!r.ok) throw new Error(r.err || "请求未能启动");
    } catch (e) {
      S.aiBusy = false;
      aiState("input");
      $("aiError").textContent = e.message;
    }
    interaction();
  };
  $("aiStop").onclick = function () {
    fire("aiCancel");
    S.aiBusy = false;
    aiState("input");
    interaction();
  };
  $("aiAgain").onclick = function () {
    aiState("input");
  };
  function renderAi() {
    $("aiPreviewCount").textContent =
      "识别到 " + S.aiItems.length + " 项，可修改后加入";
    $("aiPreviewList").innerHTML = S.aiItems
      .map(function (t, i) {
        return (
          '<article class="ai-item" data-ai="' +
          i +
          '"><div class="ai-select"><input type="checkbox" data-field="selected" aria-label="选中此任务" ' +
          (t.selected ? "checked" : "") +
          '><input type="text" maxlength="500" data-field="title" value="' +
          esc(t.title) +
          '" aria-label="任务内容"></div><div class="ai-fields"><select data-field="quadrant" aria-label="任务象限">' +
          Q.map(function (q) {
            return (
              '<option value="' +
              q[0] +
              '" ' +
              (t.quadrant === q[0] ? "selected" : "") +
              ">" +
              q[1] +
              "</option>"
            );
          }).join("") +
          '</select><select data-field="period" aria-label="计划范围">' +
          M.periods
            .map(function (p) {
              return (
                '<option value="' +
                p +
                '" ' +
                (t.period === p ? "selected" : "") +
                ">" +
                P[p] +
                "</option>"
              );
            })
            .join("") +
          '</select><label>截止 <input type="date" data-field="date" value="' +
          esc(t.date || "") +
          '"></label><input type="time" data-field="due" value="' +
          esc(t.due || "") +
          '" aria-label="截止时刻"></div></article>'
        );
      })
      .join("");
  }
  $("aiPreviewList").addEventListener("input", function (e) {
    var card = e.target.closest("[data-ai]");
    if (!card) return;
    var t = S.aiItems[+card.dataset.ai],
      f = e.target.dataset.field;
    if (f) t[f] = f === "selected" ? e.target.checked : e.target.value;
  });
  $("aiCommit").onclick = async function () {
    var selected = S.aiItems.filter(function (t) {
      return t.selected;
    });
    if (!selected.length) {
      toast("请至少选择一项");
      return;
    }
    var added;
    try {
      added = selected.map(function (t) {
        var x = M.normalize([
          Object.assign({}, t, {
            id: M.uid(),
            planDate: M.planAnchor(t.period, M.day(new Date())),
            ai: true,
          }),
        ])[0];
        delete x.selected;
        return M.validate(x);
      });
    } catch (e) {
      $("aiError").textContent = e.message;
      return;
    }
    if (
      await save(S.tasks.concat(added), "已加入 " + added.length + " 项任务")
    ) {
      S.aiItems = [];
      S.images = [];
      $("aiText").value = "";
      $("aiImages").innerHTML = "";
      hideLayer("ai");
      toast("任务已加入，可在全部计划中查看", true, function () {
        S.scope = "all";
        S.query = "";
        $("search").value = "";
        render();
      });
    }
  };
  function event(d) {
    if (d.evt === "mode") {
      applyMode(d.mode);
      return;
    }
    if (d.evt === "layout") {
      applyLayout(d.layout);
      return;
    }
    if (d.evt === "ballhost") {
      $("dockFallback").style.visibility = d.on ? "hidden" : "";
      return;
    }
    if (d.evt === "nav") {
      if (d.to === "ai") $("aiOpen").click();
      else if (d.to === "set") $("settingsOpen").click();
      else if (d.to === "askquit") $("quit").click();
      else if (d.to === "capture") $("newTitle").focus();
      else if (d.to === "task" && d.taskId) edit(d.taskId);
      return;
    }
    if (d.evt === "aiDelta") {
      $("aiStream").textContent += d.text || "";
      $("aiStream").scrollTop = $("aiStream").scrollHeight;
      return;
    }
    if (d.evt === "aiDone") {
      S.aiBusy = false;
      S.aiItems = (d.items || []).map(function (t) {
        return Object.assign(
          { selected: true, period: "day", date: "", due: "" },
          t,
        );
      });
      if (!S.aiItems.length) {
        aiState("input");
        $("aiError").textContent = "没有识别到待办，请补充描述再试。";
      } else {
        aiState("preview");
        renderAi();
      }
      interaction();
      return;
    }
    if (d.evt === "aiError") {
      S.aiBusy = false;
      aiState("input");
      $("aiError").textContent = d.message || "AI 整理失败";
      interaction();
      return;
    }
    if (d.evt === "tasksChanged") load();
  }
  async function load() {
    try {
      var r = await call("load");
      if (r.err) throw new Error(r.err);
      var data = JSON.parse(r.tasks || "[]");
      S.cfg = JSON.parse(r.cfg || "{}");
      S.demo = !!r.demo || !WV;
      S.cfg.dataPath = r.dataPath;
      S.cfg.version = r.version;
      S.cfg.hotkey = !!r.hotkey;
      S.cfg.autoStart = !!r.autoStart;
      S.tasks = M.normalize(data);
      S.undo = null;
      S.retry = null;
      $("saveError").hidden = true;
      if (S.demo && !S.tasks.length) {
        S.tasks = seed();
        var seeded = await call("saveTasks", {
          tasks: JSON.stringify(S.tasks),
        });
        if (!seeded.ok) throw new Error("示例数据无法写入隔离目录");
      }
      S.scope = ["day", "week", "month", "all"].includes(S.cfg.scope)
        ? S.cfg.scope
        : "day";
      S.list = !!S.cfg.listView;
      S.cfg.newPeriod = M.periods.includes(S.cfg.newPeriod)
        ? S.cfg.newPeriod
        : "day";
      $("newQuadrant").value = M.quadrants.includes(S.cfg.newQuadrant)
        ? S.cfg.newQuadrant
        : "q2";
      $("pinToggle").setAttribute("aria-pressed", !!S.cfg.pinned);
      $("stats").hidden = !S.cfg.statsExpanded;
      $("statsToggle").setAttribute("aria-expanded", !$("stats").hidden);
      applyLayout(S.cfg.layout || "workbench");
      S.ready = true;
      $("loadFailure").hidden = true;
      status(S.demo ? "演示数据 · 与正式数据隔离" : "已保存到本机");
      render();
    } catch (e) {
      S.ready = false;
      $("loadFailure").hidden = false;
      $("loadFailureText").textContent = e.message + "。原任务文件未被覆盖。";
      status("载入失败", true);
    }
  }
  $("reload").onclick = load;
  var down = null;
  $("titlebar").onmousedown = function (e) {
    if (e.button === 0 && !e.target.closest("button"))
      down = { x: e.screenX, y: e.screenY };
  };
  document.addEventListener("mousemove", function (e) {
    if (
      down &&
      (Math.abs(e.screenX - down.x) > 4 || Math.abs(e.screenY - down.y) > 4)
    ) {
      down = null;
      fire("drag");
    }
  });
  document.addEventListener("mouseup", function () {
    down = null;
  });
  document.addEventListener("keydown", async function (e) {
    if (e.key === "Escape") {
      if ($("confirmDialog").open) return;
      if (!$("trash").hidden) {
        hideLayer("trash");
        return;
      }
      if (!$("ai").hidden) {
        $("aiClose").click();
        return;
      }
      if (!$("settings").hidden) {
        $("settingsClose").click();
        return;
      }
      if (S.editor) {
        closeEditor();
        return;
      }
      if (S.bulk) {
        $("bulkCancel").click();
        return;
      }
      fire("collapse");
    }
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "f") {
      e.preventDefault();
      $("search").focus();
    }
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "n") {
      e.preventDefault();
      $("newTitle").focus();
    }
    if (
      (e.ctrlKey || e.metaKey) &&
      e.key.toLowerCase() === "z" &&
      !e.target.matches("input,textarea")
    ) {
      e.preventDefault();
      $("undo").click();
    }
    if (e.ctrlKey && e.key === "Enter" && S.editor) {
      e.preventDefault();
      $("editForm").requestSubmit();
    }
    if (e.key === "Tab") {
      var layer = ["trash", "ai", "settings", "editor"]
        .map($)
        .find(function (el) {
          return !el.hidden;
        });
      if (layer && !$("confirmDialog").open) {
        var controls = Array.from(
            layer.querySelectorAll("button,input,select,textarea,summary"),
          ).filter(function (el) {
            return !el.disabled && el.getClientRects().length;
          }),
          first = controls[0],
          last = controls[controls.length - 1];
        if (e.shiftKey && document.activeElement === first) {
          e.preventDefault();
          last.focus();
        } else if (!e.shiftKey && document.activeElement === last) {
          e.preventDefault();
          first.focus();
        }
      }
    }
  });
  var lastAlive = 0;
  [
    "mousedown",
    "keydown",
    "wheel",
    "input",
    "paste",
    "change",
    "focusin",
    "focusout",
  ].forEach(function (type) {
    document.addEventListener(
      type,
      function () {
        if (Date.now() - lastAlive > 400) {
          lastAlive = Date.now();
          fire("alive");
        }
        setTimeout(interaction, 0);
      },
      true,
    );
  });
  setInterval(function () {
    if (S.ready && !S.saving && !S.dragId) render();
  }, 30000);
  window.addEventListener("error", function (e) {
    console.error("TaskHarbor UI error", e.message);
  });
  window.TaskHarborApp = { state: S, reload: load };
  load();
})();
