/* TaskHarbor domain model. No DOM, network, or filesystem dependencies. MIT. */
(function (root, factory) {
  if (typeof module === "object" && module.exports) module.exports = factory();
  else root.TaskHarborModel = factory();
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  "use strict";
  var periods = ["day", "week", "month"];
  var quadrants = ["q1", "q2", "q3", "q4"];
  function pad(n) {
    return String(n).padStart(2, "0");
  }
  function day(d) {
    return (
      d.getFullYear() + "-" + pad(d.getMonth() + 1) + "-" + pad(d.getDate())
    );
  }
  function parse(s) {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(s || "")) return null;
    var a = s.split("-").map(Number),
      d = new Date(a[0], a[1] - 1, a[2], 12);
    return day(d) === s ? d : null;
  }
  function clone(x) {
    return JSON.parse(JSON.stringify(x));
  }
  function uid() {
    return (
      "th_" +
      Date.now().toString(36) +
      "_" +
      Math.random().toString(36).slice(2, 10)
    );
  }
  function range(period, anchor) {
    var n = parse(anchor) || new Date(),
      lo = new Date(n),
      hi = new Date(n);
    if (period === "week") {
      lo.setDate(n.getDate() - ((n.getDay() + 6) % 7));
      hi = new Date(lo);
      hi.setDate(lo.getDate() + 6);
    } else if (period === "month") {
      lo.setDate(1);
      hi = new Date(n.getFullYear(), n.getMonth() + 1, 0, 12);
    }
    return [day(lo), day(hi)];
  }
  function planAnchor(period, anchor) {
    return range(period, anchor)[0];
  }
  function normalize(items, now) {
    if (!Array.isArray(items)) throw new Error("任务数据必须是 JSON 数组");
    var seen = Object.create(null),
      current = day(now || new Date());
    return items.map(function (raw) {
      if (!raw || typeof raw !== "object" || typeof raw.title !== "string")
        throw new Error("任务文件含无效条目，原文件未覆盖");
      var t = clone(raw);
      if (typeof t.id !== "string" || !t.id || seen[t.id]) t.id = uid();
      seen[t.id] = true;
      t.quadrant = quadrants.includes(t.quadrant)
        ? t.quadrant
        : t.pri === "high"
          ? "q1"
          : t.pri === "low"
            ? "q4"
            : "q2";
      t.period = periods.includes(t.period) ? t.period : "day";
      // Legacy dates are real saved deadlines: never replace them with new defaults.
      t.date = typeof t.date === "string" ? t.date : "";
      t.due = typeof t.due === "string" ? t.due : "";
      t.planDate = planAnchor(
        t.period,
        parse(t.planDate) ? t.planDate : parse(t.date) ? t.date : current,
      );
      t.done = t.done === true;
      t.doneAt = t.doneAt || null;
      t.repeat = periods.includes(t.repeat) ? t.repeat : "none";
      t.subtasks = Array.isArray(t.subtasks)
        ? t.subtasks
            .filter(function (s) {
              return s && typeof s.title === "string";
            })
            .map(function (s) {
              return {
                id: s.id || uid(),
                title: s.title,
                done: s.done === true,
              };
            })
        : [];
      t.schemaVersion = 3;
      return t;
    });
  }
  function validate(t) {
    if (!t.title || !t.title.trim()) throw new Error("请填写任务内容");
    if (t.title.length > 500) throw new Error("任务内容最多 500 个字符");
    if (!quadrants.includes(t.quadrant) || !periods.includes(t.period))
      throw new Error("请选择有效的象限和计划范围");
    if (!parse(t.planDate)) throw new Error("请选择有效的计划日期");
    if (t.date && !parse(t.date)) throw new Error("截止日期无效");
    if (t.due && !t.date) throw new Error("设置截止时刻前，请先选择截止日期");
    if (t.due && !/^([01]\d|2[0-3]):[0-5]\d$/.test(t.due))
      throw new Error("截止时刻无效");
    if (t.remind && !t.date) throw new Error("提醒需要设置截止日期");
    if (t.subtasks && t.subtasks.length > 50)
      throw new Error("每条任务最多 50 个子任务");
    return t;
  }
  function deadline(t) {
    var d = parse(t.date);
    if (!d) return null;
    if (t.due && /^([01]\d|2[0-3]):[0-5]\d$/.test(t.due)) {
      var p = t.due.split(":");
      d.setHours(+p[0], +p[1], 0, 0);
    } else d.setHours(23, 59, 59, 999);
    return d;
  }
  function late(t, now) {
    var d = deadline(t);
    return !t.done && !t.deletedAt && !!d && d < (now || new Date());
  }
  function inScope(t, scope, now, includeOverdue) {
    if (t.deletedAt) return false;
    if (scope === "all") return true;
    var current = day(now || new Date());
    if (scope === "day" && includeOverdue && late(t, now)) return true;
    if (periods.indexOf(t.period) > periods.indexOf(scope)) return false;
    var r = range(scope, current),
      tr = range(t.period, t.planDate);
    return tr[0] <= r[1] && tr[1] >= r[0];
  }
  function completedDay(t) {
    var d = new Date(t.doneAt);
    return t.doneAt && !isNaN(+d) ? day(d) : "";
  }
  function stats(tasks, now, days) {
    var current = day(now || new Date()),
      active = tasks.filter(function (t) {
        return !t.deletedAt;
      });
    var doneToday = active.filter(function (t) {
      return t.done && completedDay(t) === current;
    }).length;
    var counts = Object.create(null);
    active.forEach(function (t) {
      if (t.done && completedDay(t))
        counts[completedDay(t)] = (counts[completedDay(t)] || 0) + 1;
    });
    var trend = [];
    for (var i = (days || 7) - 1; i >= 0; i--) {
      var d = parse(current);
      d.setDate(d.getDate() - i);
      var key = day(d);
      trend.push({ date: key, count: counts[key] || 0 });
    }
    return {
      pending: active.filter(function (t) {
        return !t.done;
      }).length,
      overdue: active.filter(function (t) {
        return late(t, now);
      }).length,
      doneToday: doneToday,
      trend: trend,
    };
  }
  function advance(date, unit, anchorDay) {
    var d = parse(date);
    if (!d) return "";
    if (unit === "month") {
      var dom = anchorDay || d.getDate();
      d = new Date(d.getFullYear(), d.getMonth() + 1, 1, 12);
      d.setDate(
        Math.min(dom, new Date(d.getFullYear(), d.getMonth() + 1, 0).getDate()),
      );
    } else d.setDate(d.getDate() + (unit === "week" ? 7 : 1));
    return day(d);
  }
  function complete(tasks, id, on, now) {
    var result = clone(tasks),
      t = result.find(function (x) {
        return x.id === id;
      });
    if (!t || t.deletedAt) return result;
    t.done = on;
    t.doneAt = on ? (now || new Date()).toISOString() : null;
    if (
      on &&
      t.repeat !== "none" &&
      !result.some(function (x) {
        return x.generatedFrom === id;
      })
    ) {
      var next = clone(t),
        anchor =
          t.repeatAnchorDay || (parse(t.date) || parse(t.planDate)).getDate();
      next.planDate = advance(
        t.planDate,
        t.repeat,
        t.repeatPlanDay || parse(t.planDate).getDate(),
      );
      next.date = t.date ? advance(t.date, t.repeat, anchor) : "";
      // A late completion creates the next active/future occurrence, not a backlog flood.
      for (
        var j = 0;
        j < 5000 &&
        range(next.period, next.planDate)[1] < day(now || new Date());
        j++
      ) {
        next.planDate = advance(
          next.planDate,
          t.repeat,
          t.repeatPlanDay || parse(t.planDate).getDate(),
        );
        if (next.date) next.date = advance(next.date, t.repeat, anchor);
      }
      next.id = uid();
      next.generatedFrom = id;
      next.repeatSeries = t.repeatSeries || id;
      next.repeatAnchorDay = anchor;
      next.repeatPlanDay = t.repeatPlanDay || parse(t.planDate).getDate();
      next.done = false;
      next.doneAt = null;
      next.snoozeUntil = null;
      next.subtasks.forEach(function (s) {
        s.id = uid();
        s.done = false;
      });
      result.push(next);
    }
    return result;
  }
  return {
    day: day,
    parse: parse,
    clone: clone,
    uid: uid,
    range: range,
    planAnchor: planAnchor,
    normalize: normalize,
    validate: validate,
    deadline: deadline,
    late: late,
    inScope: inScope,
    stats: stats,
    completedDay: completedDay,
    advance: advance,
    complete: complete,
    periods: periods,
    quadrants: quadrants,
  };
});
