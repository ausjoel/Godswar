"""modules/events.py - battlefield opening times and scheduled notices. Backend for static/modules/events.js.

The game server hands over its compiled windows (GET /event-content on the loopback management port) and reads the
editor's rows from event_schedule_overrides / server_notices / notice_settings (hot reload, no restart).
No row = the compiled behaviour.
"""
import datetime
import re

from . import common, world as W
from .common import Bad

M = common.module("events", "Events & notices", icon={"sprite": 684}, order=26, status="ready",
                  description="When the battlefields open, and messages the server sends by itself on a schedule.")

ACTIVITIES = ("Pindus", "NiMini")
CLOSED_DAY = -1
NOTICE_MAX = 50
WINDOW_MAX = 42
TEXT_MAX = 106                 # PacketBuilder.CenteredRedAnnouncementMaximumTextLength
NOTICE_PARTS = [("server_notices", "true", "id"), ("notice_settings", "true", "name")]
# Read from the server source. Compiled, so the page only shows them.
FIXED = [
    ("Zeus' Gift exchange", "Saturday 12:00 to Sunday 23:55, realm time"),
    ("Zeus' Gift claims", "Sunday 12:00 to 23:55, realm time"),
    ("Zeus' Gift praying stones", "Monday to Friday, realm time"),
    ("Faction Crier nameplate", "Closed on Sunday, realm time"),
    ("Daily quests", "Reset at 12:00 on the VPS clock (UTC), which is 22:00 in Brisbane"),
]


def _parts(activity):
    return [("event_schedule_overrides", "activity = %s" % common.lit(activity), "day_of_week, start_minute")]


def _scope(activity):
    return common.scope("event_schedule_overrides", "activity = %s" % common.lit(activity),
                        pk=["activity", "day_of_week", "start_minute"])


def _content(ctx):
    port = common.mgmt_port(ctx.app)
    if not port:
        raise Bad("no game server management port is configured for this database")
    st, doc = common._http("GET", port, "/event-content", timeout=8.0)
    if st != 200 or not isinstance(doc, dict):
        raise Bad("the running game server does not offer event editing yet (HTTP %s)" % st)
    return doc


def _reload(ctx):
    """Best effort: the game server also polls every 15 seconds."""
    try:
        common._http("POST", common.mgmt_port(ctx.app), "/npc-content/reload", timeout=4.0)
    except Exception:
        pass


def _activity(v):
    if v not in ACTIVITIES:
        raise Bad("unknown activity")
    return v


def _day(v, what):
    if v in (None, ""):
        return None
    try:
        return datetime.date.fromisoformat(str(v)).isoformat()
    except ValueError:
        raise Bad("%s must be a date" % what)


def check_notice_text(v, what):
    text = W._text(v, what, TEXT_MAX)
    if not re.fullmatch(r"[ -~]+", text) or "|c" in text.lower():
        raise Bad("%s: the game shows plain English letters, digits and punctuation only" % what)
    return text


def notice_row(r, index):
    what = "notice %d" % index
    start = W._int(r.get("start_minute"), 0, 1439, what + " time")
    repeat = W._int(r.get("repeat_minutes", 0), 0, 1439, what + " repeat")
    end = W._int(r.get("end_minute", start), 0, 1439, what + " last time") if repeat else start
    if end < start:
        raise Bad("%s: the last time is before the first (a notice can't run past midnight; add a second one)" % what)
    first, last = _day(r.get("first_day"), what + " first day"), _day(r.get("last_day"), what + " last day")
    if first and last and last < first:
        raise Bad("%s: the last day is before the first day" % what)
    delivery = r.get("delivery")
    if delivery not in ("banner", "log"):
        raise Bad("%s: choose how it is shown" % what)
    return {"id": index, "message": check_notice_text(r.get("message"), what), "delivery": delivery,
            "day_mask": W._int(r.get("day_mask"), 1, 127, what + " days"), "start_minute": start, "end_minute": end,
            "repeat_minutes": repeat, "first_day": first, "last_day": last, "enabled": bool(r.get("enabled", True))}


def window_rows(windows):
    if not isinstance(windows, list) or not 1 <= len(windows) <= WINDOW_MAX:
        raise Bad("a schedule has 1-%d opening times (or mark it closed)" % WINDOW_MAX)
    rows, seen = [], set()
    for i, w in enumerate(windows):
        what = "opening %d" % (i + 1)
        key = (W._int(w.get("day"), 0, 6, what + " day"), W._int(w.get("start_minute"), 0, 1439, what + " time"))
        if key in seen:
            raise Bad("%s: this day and time is listed twice" % what)
        seen.add(key)
        rows.append({"day_of_week": key[0], "start_minute": key[1],
                     "duration_minutes": W._int(w.get("duration_minutes"), 1, 1440, what + " length")})
    return rows


@M.get("overview")
def overview(ctx):
    doc = _content(ctx)
    over = W._q(ctx, "(SELECT coalesce(json_agg(to_jsonb(o) ORDER BY day_of_week, start_minute), '[]') FROM event_schedule_overrides o)")
    for a in doc["activities"]:
        a["override"] = [o for o in over if o["activity"] == a["key"]] or None
        a["version"] = W._version(ctx, _parts(a["key"]))
    notices = W._q(ctx, "(SELECT coalesce(json_agg(to_jsonb(n) ORDER BY id), '[]') FROM server_notices n)")
    stored = W._q(ctx, "(SELECT coalesce(json_object_agg(name, value), '{}') FROM notice_settings)")
    doc.update(notices=notices, notices_version=W._version(ctx, NOTICE_PARTS), fixed=[{"name": n, "when": w} for n, w in FIXED],
               population_minutes=stored.get("population_minutes", doc["population_default"]),
               population_saved=stored.get("population_minutes_saved") or doc["population_default"] or 5, tick_seconds=15)
    return doc


@M.post("schedule/save")
def schedule_save(ctx):
    b = ctx.body
    activity = _activity(b.get("activity"))
    rows = [{"day_of_week": CLOSED_DAY, "start_minute": 0, "duration_minutes": 1}] if b.get("closed") else window_rows(b.get("windows"))
    stmts = [W._ver_guard(_parts(activity), b.get("version"), "this schedule"),
             "DELETE FROM event_schedule_overrides WHERE activity = %s" % common.lit(activity)]
    stmts += [common.insert_sql("event_schedule_overrides", dict(r, activity=activity)) for r in rows]
    out = W._write(ctx, [_scope(activity)], "%s schedule: %s" % (activity, "closed" if b.get("closed") else "%d openings" % len(rows)),
                   stmts, ["event_schedule_overrides.*"], target="schedule " + activity)
    _reload(ctx)
    return out


@M.post("schedule/reset")
def schedule_reset(ctx):
    activity = _activity(ctx.body.get("activity"))
    out = W._write(ctx, [_scope(activity)], "%s schedule: back to the original times" % activity,
                   [W._ver_guard(_parts(activity), ctx.body.get("version"), "this schedule"),
                    "DELETE FROM event_schedule_overrides WHERE activity = %s" % common.lit(activity)],
                   ["event_schedule_overrides.*"], target="schedule " + activity)
    _reload(ctx)
    return out


@M.post("notices/save")
def notices_save(ctx):
    b = ctx.body
    rows = b.get("rows")
    if not isinstance(rows, list) or len(rows) > NOTICE_MAX:
        raise Bad("the list must have 0-%d notices" % NOTICE_MAX)
    if "population_enabled" in b:
        # An explicit switch: the server reads only population_minutes (0 = off), so "off" stores 0 there and keeps the
        # chosen interval in population_minutes_saved (a name the server ignores) for when it is switched back on.
        enabled = bool(b.get("population_enabled"))
        interval = W._int(b.get("population_minutes"), 1, 1440, "population notice interval")
        population = interval if enabled else 0
        extra = [] if enabled else [common.insert_sql("notice_settings", {"name": "population_minutes_saved", "value": interval})]
    else:
        population = W._int(b.get("population_minutes"), 0, 1440, "population notice interval")
        extra = []
    stmts = [W._ver_guard(NOTICE_PARTS, b.get("version"), "the notices"), "DELETE FROM server_notices", "DELETE FROM notice_settings"]
    stmts += [common.insert_sql("server_notices", notice_row(r, i + 1)) for i, r in enumerate(rows)]
    stmts.append(common.insert_sql("notice_settings", {"name": "population_minutes", "value": population}))
    stmts += extra
    out = W._write(ctx, [common.scope("server_notices", "true", pk=["id"]), common.scope("notice_settings", "true", pk=["name"])],
                   "notices: %d, population line %s" % (len(rows), "every %d min" % population if population else "off"), stmts,
                   ["server_notices.*", "notice_settings.*"], target="notices")
    _reload(ctx)
    return out
