/* static/modules/events.js - battlefield opening times and scheduled notices. Backend: modules/events.py. */
(function () {
  "use strict";
  const GW = window.GW, h = GW.h;
  const DAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
  const hhmm = m => String(Math.floor(m / 60)).padStart(2, "0") + ":" + String(m % 60).padStart(2, "0");
  const minutes = s => { const p = String(s).split(":"); return Number(p[0]) * 60 + Number(p[1]); };
  const time = (value, label, onchange) => h("input", { type: "time", value: hhmm(value), "aria-label": label, onchange: e => onchange(minutes(e.target.value)) });
  const num = (value, label, o) => h("input", Object.assign({ type: "number", value, "aria-label": label, class: "gw-ev-num" }, o || {}));

  GW.module("events", {
    async mount(el, ctx) {
      let D = null;
      const root = h("div");
      el.append(root);
      async function load() { D = await ctx.api("overview"); draw(); }
      async function write(route, body, done) {
        try { GW.writeDone(await ctx.api(route, body), done); await load(); }
        catch (e) { if (await GW.writeError(e) === "conflict") await load(); }
      }
      function draw() {
        GW.clear(root).append(
          h("p", { class: "gw-dim" }, "Realm time now: " + (D.realm_time || "?") + " (" + (D.time_zone || "?") + "). Saved changes reach the game within 15 seconds, no restart."),
          h("h2", null, "Battlefield opening times"),
          ...D.activities.map(drawActivity),
          h("h2", null, "Scheduled notices"),
          drawNotices(),
          h("h2", null, "Other fixed times"),
          h("p", { class: "gw-dim" }, "Compiled into the server, shown here for reference. Not editable."),
          h("table", { class: "gw-ev-fixed" }, h("tbody", null, D.fixed.map(f => h("tr", null, h("td", null, f.name), h("td", null, f.when))))));
      }
      function drawActivity(a) {
        const edited = a.override && a.override.length;
        const closed = edited && a.override.some(w => w.day_of_week === -1);
        let rows = edited && !closed ? a.override.map(w => ({ day: w.day_of_week, start_minute: w.start_minute, duration_minutes: w.duration_minutes })) : a.compiled.map(w => ({ day: w.day, start_minute: w.start_minute, duration_minutes: w.duration_minutes }));
        const body = h("div");
        function drawRows() {
          GW.clear(body);
          rows.forEach((r, i) => body.append(h("div", { class: "gw-ev-row" },
            h("select", { "aria-label": a.name + " day", onchange: e => { r.day = Number(e.target.value); } }, DAYS.map((d, n) => h("option", { value: n, selected: n === r.day }, d))),
            time(r.start_minute, a.name + " opening time", v => { r.start_minute = v; }),
            h("label", null, "for ", num(r.duration_minutes, a.name + " length in minutes", { min: 1, max: 1440, onchange: e => { r.duration_minutes = Number(e.target.value); } }), " min"),
            h("button", { class: "gw-btnx small", type: "button", onclick: () => { rows.splice(i, 1); drawRows(); } }, "Remove"))));
        }
        drawRows();
        const notes = [];
        if (a.always_open) notes.push("The \"always open\" switch on the rates tab is on, so these times have no effect until it is off.");
        if (!edited && (D.window_minutes !== 45 || D.shift_hours !== 0)) notes.push("The rates tab currently changes these to " + D.window_minutes + " minutes, shifted " + D.shift_hours + " hours. Saving here replaces that for this battlefield.");
        return h("section", { class: "gw-ev-card" },
          h("h3", null, a.name, " ", h("span", { class: "gw-chip" }, closed ? "closed" : edited ? "edited" : "original times")),
          ...notes.map(n => h("p", { class: "gw-dim" }, n)),
          body,
          h("div", { class: "gw-ev-actions" },
            h("button", { class: "gw-btnx", type: "button", onclick: () => { rows.push({ day: 6, start_minute: 720, duration_minutes: 45 }); drawRows(); } }, "Add opening"),
            h("button", { class: "gw-btnx primary", type: "button", onclick: () => write("schedule/save", { activity: a.key, windows: rows, version: a.version }, a.name + " saved") }, "Save"),
            h("button", { class: "gw-btnx", type: "button", onclick: () => write("schedule/save", { activity: a.key, closed: true, version: a.version }, a.name + " closed") }, "Close it"),
            edited ? h("button", { class: "gw-btnx", type: "button", onclick: () => write("schedule/reset", { activity: a.key, version: a.version }, a.name + " back to the original times") }, "Original times") : null));
      }
      function drawNotices() {
        const rows = D.notices.map(n => Object.assign({}, n));
        let populationOn = D.population_minutes > 0;
        let population = populationOn ? D.population_minutes : (D.population_saved || 5);
        const body = h("div");
        function drawRows() {
          GW.clear(body);
          if (!rows.length) body.append(h("p", { class: "gw-dim" }, "None. The server sends nothing but the population line until you add one."));
          rows.forEach((n, i) => {
            const label = "Notice " + (i + 1);
            body.append(h("div", { class: "gw-ev-notice" },
              h("input", { type: "text", class: "gw-ev-text", maxlength: 106, value: n.message, placeholder: "Plain English, up to 106 characters", "aria-label": label + " text", oninput: e => { n.message = e.target.value; } }),
              h("div", { class: "gw-ev-row" },
                h("select", { "aria-label": label + " shown as", onchange: e => { n.delivery = e.target.value; } },
                  h("option", { value: "banner", selected: n.delivery === "banner" }, "Red banner"), h("option", { value: "log", selected: n.delivery === "log" }, "Game log line")),
                DAYS.map((d, b) => h("label", { class: "gw-ev-day" }, h("input", { type: "checkbox", checked: !!(n.day_mask & (1 << b)), "aria-label": label + " " + d, onchange: e => { n.day_mask = e.target.checked ? n.day_mask | (1 << b) : n.day_mask & ~(1 << b); } }), d)),
                h("label", null, "at ", time(n.start_minute, label + " time", v => { n.start_minute = v; })),
                h("label", null, "repeat every ", num(n.repeat_minutes, label + " repeat in minutes, 0 for once", { min: 0, max: 1439, onchange: e => { n.repeat_minutes = Number(e.target.value); } }), " min (0 = once)"),
                h("label", null, "until ", time(n.end_minute, label + " last time", v => { n.end_minute = v; })),
                h("label", null, "from ", h("input", { type: "date", value: n.first_day || "", "aria-label": label + " first day", onchange: e => { n.first_day = e.target.value || null; } })),
                h("label", null, "to ", h("input", { type: "date", value: n.last_day || "", "aria-label": label + " last day", onchange: e => { n.last_day = e.target.value || null; } })),
                h("label", null, h("input", { type: "checkbox", checked: n.enabled, "aria-label": label + " on", onchange: e => { n.enabled = e.target.checked; } }), "on"),
                h("button", { class: "gw-btnx small", type: "button", onclick: () => { rows.splice(i, 1); drawRows(); } }, "Remove"))));
          });
        }
        drawRows();
        return h("section", { class: "gw-ev-card" },
          h("div", { class: "gw-ev-row" },
            h("label", { "data-help": "The line 'Online: N | Athenians: A | Spartans: S' shown to every player in the world. Off = it is never sent." }, h("input", { type: "checkbox", checked: populationOn, "aria-label": "Show the online-players notice", onchange: e => { populationOn = e.target.checked; } }), " Show the online-players notice"),
            h("label", null, " every ", num(population, "Online-players notice interval in minutes", { min: 1, max: 1440, onchange: e => { population = Math.max(1, Number(e.target.value) || 1); } }), " min")),
          h("p", { class: "gw-dim" }, "The server checks the schedule every " + (D.tick_seconds || 15) + " seconds (fixed in the server), so the line appears within " + (D.tick_seconds || 15) + " seconds of the interval being reached. Saving applies it within 15 seconds; no restart."),
          body,
          h("div", { class: "gw-ev-actions" },
            h("button", { class: "gw-btnx", type: "button", onclick: () => { rows.push({ message: "", delivery: "banner", day_mask: 127, start_minute: 720, end_minute: 720, repeat_minutes: 0, first_day: null, last_day: null, enabled: true }); drawRows(); } }, "Add notice"),
            h("button", { class: "gw-btnx primary", type: "button", onclick: () => write("notices/save", { rows, population_enabled: populationOn, population_minutes: population, version: D.notices_version }, "Notices saved") }, "Save notices")));
      }
      await load();
    }
  });
})();
