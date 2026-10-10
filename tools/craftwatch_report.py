#!/usr/bin/env python3
"""Render CraftWatch log files as one HTML page.

The page answers three questions per log file:
  1. When did the bug occur?
  2. Which recipe was being crafted and is now broken?
  3. Which handbook recipe probably caused it?

Usage:
    python craftwatch_report.py [LOG_OR_DIR ...] [-o report.html] [--open]

Without arguments it reads %APPDATA%\\VintagestoryData\\CraftWatch and writes
craftwatch-report.html next to the logs. Only the standard library is used.

Logs of CraftWatch 0.1.0 and 0.2.0 are in German, later ones in English; both are understood.
The handbook page and the item that was rendered are only recorded from 0.3.0 on.
"""

from __future__ import annotations

import argparse
import html
import os
import re
import sys
import webbrowser
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

LINE = re.compile(r"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) \[(.)\] (\S+)\s+(.*)$")
RECIPE_KEY = re.compile(r"((?:[\w.-]+:)?recipes/[^\s;,\]]+->[^\s;,\]]+)")
WRITE = re.compile(r"(?:Schreibzugriff|write) #(\d+) (?:auf|to) defaultEmptyAttributes: (\w+)\(\"([^\"]*)\"\)(?: item=(\S+))?")
DAMAGE = re.compile(
    r"(?:gemeinsame Zutaten-Stacks|shared ingredient stacks): (\d+) .*?"
    r"(?:Schnellpfad erkennt bei|own item for) (\d+).*?"
    r"(?:Rezeptbezuege|recipe references): (\d+)\)\. (?:Beispiele|Examples): (.*?)\. (?:Attribute|Attributes): (.*)$")
GRID = re.compile(r"^(\S+): in=\[(.*?)\] out=(\S+) (?:referenz|reference|eigeneRechnung)=\[(.*?)\](?: (?:schnellpfad|fastPath)=\[(.*?)\])?")
RECIPE_DEF = re.compile(r"(?:REZEPT|RECIPE) (\S+) (\d+)x(\d+) shapeless=(\w+) out=(\w+):(\S+) q=(\d+)")
INGREDIENT_DEF = re.compile(r"\[(\d+)\] (\w+):(\S+) q=(\d+)")
INGREDIENT_RAW = re.compile(
    r"(?:zutat|ingredient)(\d+) (\S+): (?:ROH|RAW) (.*?)(?: (?:eigenesItem|acceptsOwnItem)=(\w+))?(?: (?:schnellpfadGegenGrid|fastPathVsGrid)=(\w+))?(?= \|\| |$)")
CAUSE_ITEM = re.compile(r"^item=(\S+) key=(\S*) handbookPage=(.*?) dialogs=\[(.*)\]$")
CAUSE_RECIPE = re.compile(r"^recipe kind=(\S+) code=(\S+) sealHours=(\S+) in=\[(.*?)\] out=(\S+) pageMentions=(\w+)$")
FRAME = re.compile(r"^\s+at (.+?)(?: in (.+?):line (\d+))?$")

KEY_KINDS = {"TRACE", "CAUSE", "ATTRS", "ALARM", "MARK"}


@dataclass
class Entry:
    index: int
    time: datetime
    side: str
    kind: str
    message: str
    extra: list[str] = field(default_factory=list)
    tag: str = ""        # short machine label: write, damage, signature, report, canary, ...
    key: bool = False    # one of the moments the page points to


@dataclass
class Session:
    path: Path
    entries: list[Entry]
    mod: str = "?"
    game: str = "?"
    cause: Entry | None = None
    damage: dict[str, Entry] = field(default_factory=dict)
    symptom: Entry | None = None


def parse(path: Path) -> Session:
    entries: list[Entry] = []
    with path.open(encoding="utf-8", errors="replace") as handle:
        for raw in handle:
            raw = raw.rstrip("\r\n")
            match = LINE.match(raw)
            if match:
                stamp, side, kind, message = match.groups()
                entries.append(Entry(len(entries), datetime.strptime(stamp, "%Y-%m-%d %H:%M:%S.%f"), side, kind, message))
            elif entries and raw.strip():
                entries[-1].extra.append(raw)

    session = Session(path, entries)
    for entry in entries:
        classify(entry)
        if entry.kind == "START":
            found = re.search(r"CraftWatch (\S+?), (?:Spiel|game) (\S+?),", entry.message)
            if found:
                session.mod, session.game = found.groups()

    # The three moments. The server decides whether crafting works, so its view wins.
    session.cause = first(entries, lambda e: e.tag == "write") or first(entries, lambda e: e.tag == "attrs-filled")
    since = session.cause.time if session.cause else datetime.min
    for side in "SC":
        hit = first(entries, lambda e, s=side: e.side == s and e.time >= since and is_damage(e))
        if hit:
            session.damage[side] = hit
    session.symptom = (first(entries, lambda e: e.tag == "signature" and e.side == "S")
                       or first(entries, lambda e: e.tag == "signature"))
    for moment in [session.cause, session.symptom, *session.damage.values()]:
        if moment:
            moment.key = True
    return session


def first(entries, predicate):
    return next((entry for entry in entries if predicate(entry)), None)


def is_damage(entry: Entry) -> bool:
    """A line that reports ingredients the fast path no longer accepts."""
    if entry.tag not in ("damage", "damage-update"):
        return False
    match = DAMAGE.search(entry.message)
    return match is None or int(match.group(2)) > 0      # no match: the per-stack lines of 0.1.0


def classify(entry: Entry) -> None:
    message = entry.message
    if entry.kind == "TRACE" and WRITE.search(message):
        entry.tag = "write"
    elif entry.kind == "ATTRS" and ("BEFUELLT" in message or "NOT EMPTY" in message):
        entry.tag = "attrs-filled"
    elif entry.kind == "ALARM":
        if "CANARY" in message:
            entry.tag = "canary"
        elif message.startswith(("MELDUNG", "REPORT")):
            entry.tag = "report"
        elif message.startswith(("SIGNATUR", "MISMATCH")):
            entry.tag = "signature"
        elif DAMAGE.search(message):
            entry.tag = "damage"
    elif entry.kind == "SHARED":
        if DAMAGE.search(message):
            entry.tag = "damage-update"
        elif "veraendert" in message:
            entry.tag = "damage"          # 0.1.0 logs one line per stack
    elif entry.kind == "GUI" and "Handbook" in message:
        entry.tag = "handbook"
    elif entry.kind == "GUI" and message.startswith("handbook page:"):
        entry.tag = "handbook"


# ----------------------------------------------------------------------------- facts

def handbook_state(session: Session, at: datetime):
    """(dialog name, opened at, page) of the handbook as it was at the given time."""
    name, opened, page = None, None, None
    for entry in session.entries:
        if entry.time > at:
            break
        if entry.kind != "GUI":
            continue
        message = entry.message
        if message.startswith("handbook page:"):
            page = message.split(":", 1)[1].strip()
        elif "Handbook" in message:
            if message.startswith(("auf:", "open:")):
                name = re.sub(r"\s*\(.*\)$", "", message.split(":", 1)[1]).strip()
                opened, page = entry.time, None
            elif message.startswith(("zu:", "close:")):
                name, opened = None, None
    return name, opened, page


def culprit_frames(entry: Entry) -> list[tuple[str, str]]:
    """Game frames of a stack trace, innermost first, without runtime and mod frames."""
    frames = []
    for line in entry.extra:
        match = FRAME.match(line)
        if not match:
            continue
        method, source, number = match.groups()
        if method.startswith(("System.", "HarmonyLib.", "CraftWatch.", "OpenTK.")) or "_Patch" in method:
            continue
        where = f"{Path(source.replace(chr(92), '/')).name}:{number}" if source else ""
        frames.append((re.sub(r"\(.*\)$", "", method), where))
    return frames


def cause_details(session: Session):
    """Item, handbook page and candidate recipes recorded right after the write (0.3.0+)."""
    cause = session.cause
    item = page = None
    recipes = []
    if cause is None:
        return item, page, recipes
    found = WRITE.search(cause.message)
    if found and found.group(4) and found.group(4) != "?":
        item = found.group(4)
    for entry in session.entries[cause.index:cause.index + 400]:
        if entry.kind != "CAUSE":
            continue
        match = CAUSE_ITEM.match(entry.message)
        if match:
            if match.group(1) != "?":
                item = item or match.group(1)
            if match.group(3) not in ("-", ""):
                page = page or match.group(3)
            continue
        match = CAUSE_RECIPE.match(entry.message)
        if match:
            kind, code, hours, inputs, output, mentions = match.groups()
            recipes.append({"kind": kind, "code": code, "hours": hours, "inputs": inputs.split(" + "),
                            "output": output, "mentions": mentions == "True"})
    return item, page, recipes


def group_recipes(recipes, item):
    """Merge the variants of one recipe: same code and output, differing in the other ingredients."""
    groups = {}
    for recipe in recipes:
        group = groups.setdefault((recipe["kind"], recipe["code"], recipe["output"], recipe["hours"]),
                                  {"others": [], "mentions": False})
        group["mentions"] |= recipe["mentions"]
        for code in recipe["inputs"]:
            if code != item and code not in group["others"]:
                group["others"].append(code)
    return sorted(groups.items(), key=lambda pair: not pair[1]["mentions"])


def grid_before(session: Session, entry: Entry):
    """The grid line of the same side that led to the entry."""
    for candidate in reversed(session.entries[:entry.index]):
        if candidate.kind == "GRID" and candidate.side == entry.side:
            match = GRID.match(candidate.message)
            if match:
                return candidate, match
    return None, None


def recipe_definition(session: Session, entry: Entry, side: str):
    """Recipe breakdown from the DIAG lines that follow an alarm."""
    for candidate in session.entries[entry.index:entry.index + 12]:
        if candidate.kind == "DIAG" and candidate.side == side:
            match = RECIPE_DEF.search(candidate.message)
            if match:
                head = candidate.message.split(" || ")[0]
                return match, INGREDIENT_DEF.findall(head), INGREDIENT_RAW.findall(candidate.message)
    return None, [], []


def short(code: str) -> str:
    return code.split(":", 1)[1] if ":" in code else code


def clock(moment: datetime) -> str:
    return moment.strftime("%H:%M:%S.") + f"{moment.microsecond // 1000:03d}"


def delta(later: datetime, earlier: datetime) -> str:
    seconds = (later - earlier).total_seconds()
    if abs(seconds) < 120:
        return f"{seconds:+.1f} s"
    return f"{seconds / 60:+.1f} min"


# ----------------------------------------------------------------------------- html

E = html.escape


def anchor(session_id: str, entry: Entry) -> str:
    return f"{session_id}-e{entry.index}"


def jump(session_id: str, entry: Entry, text: str) -> str:
    return f'<a class="jump" href="#{anchor(session_id, entry)}">{E(text)}</a>'


def code_list(codes) -> str:
    return ", ".join(f"<code>{E(short(code))}</code>" for code in codes)


def card_cause(session: Session, sid: str) -> str:
    cause = session.cause
    if cause is None:
        return '<section class="card ok"><h3>1 · Cause</h3><p>No write to the static field in this log.</p></section>'

    rows = []
    found = WRITE.search(cause.message)
    key = found.group(3) if found else None
    rows.append(("When", f"<b>{clock(cause.time)}</b> {jump(sid, cause, 'show in log')}"))

    name, opened, page = handbook_state(session, cause.time)
    item, logged_page, recipes = cause_details(session)
    page = logged_page or page
    records_context = any(entry.kind == "CAUSE" for entry in session.entries)
    missing = ('<span class="muted">none</span>' if records_context
               else '<span class="muted">not recorded (needs CraftWatch 0.3.0 or newer)</span>')
    if name:
        rows.append(("Handbook", f"open since {clock(opened)} ({delta(cause.time, opened).lstrip('+')} before the write)"))
    else:
        rows.append(("Handbook", "not open at that moment"))
    rows.append(("Handbook page", f"<code>{E(page)}</code>" if page else missing))
    rows.append(("Item rendered", f"<code>{E(item)}</code>" if item else missing))
    if key:
        note = " — the spoilage state of a perishable item" if key == "transitionstate" else ""
        rows.append(("Written", f"attribute <code>{E(key)}</code>{note}"))

    filled = first(session.entries[cause.index:], lambda e: e.tag == "attrs-filled")
    if filled:
        content = filled.message.split(":", 1)[1].strip() if ":" in filled.message else filled.message
        hours = re.search(r'"freshHours": \[([\d.]+)\].*?"transitionHours": \[([\d.]+)\]', content)
        if hours:
            rows.append(("Spoilage data", f"fresh {E(hours.group(1))} h, then spoils over {E(hours.group(2))} h"))

    frames = culprit_frames(cause)
    if frames:
        listing = "".join(f"<li><code>{E(method)}</code> <span class='muted'>{E(where)}</span></li>" for method, where in frames[:10])
        rows.append(("Call stack", f"<details><summary>{E(frames[0][0].split('.')[-1])} ← … ← "
                                   f"{E(next((m for m, _ in frames if 'Handbook' in m), frames[min(len(frames), 10) - 1][0]).split('.')[-2])}"
                                   f"</summary><ol class='frames'>{listing}</ol></details>"))

    if recipes:
        items = []
        for (kind, code, output, hours), group in group_recipes(recipes, item):
            mark = ' <span class="chip hit">named by the open page</span>' if group["mentions"] else ""
            others = f" + one of {code_list(group['others'])}" if len(group["others"]) > 1 else \
                     (f" + {code_list(group['others'])}" if group["others"] else "")
            items.append(f"<li><b>{E(kind)} recipe <code>{E(code)}</code></b>{mark}<br>"
                         f"<code>{E(short(item or '?'))}</code>{others} → <code>{E(short(output))}</code>"
                         f" <span class='muted'>(sealed {E(hours)} h)</span></li>")
        rows.append(("Probable recipe", f"<ul class='recipes'>{''.join(items)}</ul>"
                                        "<p class='muted small'>Recipes that use the rendered item as an ingredient. "
                                        "The handbook draws such ingredient stacks without cloning them.</p>"))
    else:
        rows.append(("Probable recipe", '<span class="muted">no barrel recipe uses this item</span>' if records_context else
                                        '<span class="muted">not recorded. With CraftWatch 0.3.0 or newer the log names the item '
                                        'and the recipes that use it.</span>'))

    body = "".join(f"<tr><th>{E(label)}</th><td>{value}</td></tr>" for label, value in rows)
    return f'<section class="card bad"><h3>1 · Cause: write to the shared static attributes</h3><table class="facts">{body}</table></section>'


def card_damage(session: Session, sid: str) -> str:
    if not session.damage:
        return '<section class="card ok"><h3>2 · Damage</h3><p>No corrupted ingredient stacks in this log.</p></section>'

    rows = []
    for side, label in (("S", "Server"), ("C", "Client")):
        entry = session.damage.get(side)
        if entry is None:
            rows.append((label, '<span class="muted">nothing reported</span>'))
            continue
        match = DAMAGE.search(entry.message)
        since = f" ({delta(entry.time, session.cause.time)} after the write)" if session.cause else ""
        if match:
            stacks, broken, references, examples, _ = match.groups()
            rows.append((label, f"<b>{clock(entry.time)}</b>{since} {jump(sid, entry, 'show in log')}<br>"
                                f"<b>{E(broken)}</b> ingredients no longer recognised as their own item, "
                                f"{E(stacks)} stacks carry foreign attributes, {E(references)} recipe references affected"
                                f"<br><span class='muted small'>e.g. {code_list(examples.split(', ')[:6])}</span>"))
        else:
            count = sum(1 for e in session.entries if e.tag == "damage" and e.side == side)
            rows.append((label, f"<b>{clock(entry.time)}</b>{since} {jump(sid, entry, 'show in log')}<br>"
                                f"at least {count} stacks changed <span class='muted'>(CraftWatch 0.1.0 logs one line per stack and cuts off)</span>"))

    recovered = first(session.entries, lambda e: e.kind == "SHARED" and ("wieder ohne Attribute" in e.message or "without attributes again" in e.message)
                      and session.cause is not None and e.time > session.cause.time)
    if recovered:
        rows.append(("Recovered", f"{clock(recovered.time)} on {'server' if recovered.side == 'S' else 'client'} {jump(sid, recovered, 'show in log')}"))
    elif any(DAMAGE.search(entry.message) for entry in session.damage.values()):
        last = session.entries[-1].time
        start = min(entry.time for entry in session.damage.values())
        rows.append(("Recovered", f"no — still broken at the last line of the log, {delta(last, start).lstrip('+')} after the damage"))
    else:
        rows.append(("Recovered", '<span class="muted">not recorded by CraftWatch 0.1.0, which repaired the stacks itself while observing</span>'))

    body = "".join(f"<tr><th>{E(label)}</th><td>{value}</td></tr>" for label, value in rows)
    return f'<section class="card bad"><h3>2 · Damage: ingredients the game no longer recognises</h3><table class="facts">{body}</table></section>'


def card_symptom(session: Session, sid: str) -> str:
    signatures = [entry for entry in session.entries if entry.tag == "signature"]
    if not signatures:
        text = "No failed craft was recorded." if session.cause is None else \
               "The corruption is in the log, but no craft was attempted afterwards (or the mod repaired it first, as 0.1.0 did)."
        return f'<section class="card {"ok" if session.cause is None else "warn"}"><h3>3 · Failed craft</h3><p>{E(text)}</p></section>'

    seen, blocks = set(), []
    for entry in signatures:
        keys = RECIPE_KEY.findall(entry.message)
        if not keys or (entry.side, keys[0]) in seen:
            continue
        seen.add((entry.side, keys[0]))
        if entry.side != "S" and any(side == "S" and key == keys[0] for side, key in seen):
            continue                      # the server already told this story
        recipe_file, output = keys[0].split("->", 1)
        since = f" ({delta(entry.time, session.cause.time)} after the write)" if session.cause else ""
        rows = [("When", f"<b>{clock(entry.time)}</b>{since} {jump(sid, entry, 'show in log')}"),
                ("Recipe", f"<b><code>{E(short(output))}</code></b> <span class='muted'>from {E(short(recipe_file))}</span>")]

        definition, ingredients, _ = recipe_definition(session, entry, entry.side)
        if ingredients:
            rows.append(("Needs", " + ".join(f"{E(quantity)}× <code>{E(short(code))}</code>" for _, _, code, quantity in ingredients)))

        grid_entry, grid = grid_before(session, entry)
        if grid:
            rows.append(("In the grid", f"<code>{E(grid.group(2))}</code>"))
            rows.append(("Output slot", f"<b>{E('empty' if grid.group(3) in ('leer', 'empty') else grid.group(3))}</b> on the "
                                        f"{'server' if entry.side == 'S' else 'client'}"))

        table = ingredient_table(session, entry)
        if table:
            rows.append(("Ingredient check", table))

        body = "".join(f"<tr><th>{E(label)}</th><td>{value}</td></tr>" for label, value in rows)
        blocks.append(f'<table class="facts">{body}</table>')

    return ('<section class="card bad"><h3>3 · Failed craft: the recipe matches, the output slot stays empty</h3>'
            + "<hr>".join(blocks) + "</section>")


def ingredient_table(session: Session, entry: Entry) -> str:
    """Server and client view of the recipe's ingredients at the moment of the alarm."""
    views = {}
    window = session.entries[max(0, entry.index - 12):entry.index + 14]
    for side in "SC":
        for candidate in window:
            if candidate.kind == "DIAG" and candidate.side == side and RECIPE_DEF.search(candidate.message):
                raw = INGREDIENT_RAW.findall(candidate.message)
                if raw:
                    views[side] = raw
                    break
    if not views:
        return ""

    codes = []
    for raw in views.values():
        for _, code, *_ in raw:
            if code not in codes:
                codes.append(code)

    def cell(side, code):
        for _, candidate, state, own, grid in views.get(side, []):
            if candidate != code:
                continue
            verdict = grid or own
            if verdict == "False":
                reason = "foreign attributes" if "transitionstate" in state or "/1" in state or "/2" in state else "rejected"
                return f'<td class="no">rejected <span class="small">({E(reason)})</span></td>'
            if verdict == "True":
                return '<td class="yes">accepted</td>'
            return f'<td class="muted">{E(state[:60])}</td>'
        return '<td class="muted">—</td>'

    head = "<tr><th>Ingredient</th><th>Server</th><th>Client</th></tr>"
    body = "".join(f"<tr><td><code>{E(short(code))}</code></td>{cell('S', code)}{cell('C', code)}</tr>" for code in codes)
    return f'<table class="mini">{head}{body}</table>'


def verdict(session: Session) -> tuple[str, str]:
    if session.symptom:
        return "bad", f"Bug occurred: crafting failed at {clock(session.symptom.time)}"
    if session.cause or session.damage:
        moment = session.cause or next(iter(session.damage.values()))
        return "warn", f"Corruption at {clock(moment.time)}, no failed craft recorded"
    return "ok", "No bug in this log"


def row_class(entry: Entry) -> str:
    classes = [f"k-{entry.kind}", f"s-{entry.side if entry.side in 'SC' else 'X'}"]
    if entry.key:
        classes.append("keymoment")
    if entry.kind in KEY_KINDS or entry.tag == "handbook":
        classes.append("important")
    return " ".join(classes)


def render_message(entry: Entry) -> str:
    message = entry.message
    limit = 260
    if len(message) > limit:
        text = f"<details><summary>{E(message[:limit])}…</summary><div class='long'>{E(message)}</div></details>"
    else:
        text = E(message)
    if entry.extra:
        text += f"<details><summary class='muted'>{len(entry.extra)} more lines</summary><pre>{E(chr(10).join(entry.extra))}</pre></details>"
    return text


def timeline(session: Session, sid: str) -> str:
    rows, entries, i = [], session.entries, 0
    cause_time = session.cause.time if session.cause else None
    labels = {}
    if session.cause:
        labels[session.cause.index] = "1 · cause"
    for side, entry in session.damage.items():
        labels[entry.index] = "2 · damage"
    if session.symptom:
        labels[session.symptom.index] = "3 · failed craft"

    def fold_key(entry):
        if entry.key:
            return None
        if entry.tag in ("canary", "damage", "damage-update") or entry.kind == "DIAG":
            return (entry.side, entry.kind, entry.tag)
        return None

    while i < len(entries):
        entry = entries[i]
        key = fold_key(entry)
        j = i
        if key is not None:
            while j + 1 < len(entries) and fold_key(entries[j + 1]) == key:
                j += 1
        if j - i >= 4:
            run = entries[i:j + 1]
            what = {"canary": "counter-check alarms (false alarms of 0.1.0: recipes that need a character trait)",
                    "damage": "changed ingredient stacks, one line each"}.get(entry.tag, f"{entry.kind} lines")
            inner = "".join(row(sid, e, cause_time, labels) for e in run)
            rows.append(f'<tr class="fold k-{entry.kind} s-{entry.side}"><td>{clock(entry.time)}</td><td></td>'
                        f'<td><span class="side">{E(entry.side)}</span></td><td><span class="kind">{E(entry.kind)}</span></td>'
                        f'<td><details><summary>{len(run)} × {E(what)}</summary><table class="log inner">{inner}</table></details></td></tr>')
            i = j + 1
            continue
        rows.append(row(sid, entry, cause_time, labels))
        i += 1
    return "".join(rows)


def row(sid: str, entry: Entry, cause_time, labels) -> str:
    relative = delta(entry.time, cause_time) if cause_time else ""
    label = f'<span class="chip hit">{E(labels[entry.index])}</span> ' if entry.index in labels else ""
    return (f'<tr id="{anchor(sid, entry)}" class="{row_class(entry)}"><td>{clock(entry.time)}</td><td class="rel">{E(relative)}</td>'
            f'<td><span class="side">{E(entry.side)}</span></td><td><span class="kind">{E(entry.kind)}</span></td>'
            f'<td>{label}{render_message(entry)}</td></tr>')


CSS = """
:root{--bg:#f6f5f1;--panel:#fff;--ink:#1d2125;--muted:#6b7480;--line:#dcdad3;--bad:#b3261e;--badbg:#fdecea;
--warn:#8a5a00;--warnbg:#fff4d6;--ok:#1e6b3a;--okbg:#e6f4ea;--accent:#1f5fae;--code:#efede6;--hit:#ffe08a}
@media (prefers-color-scheme:dark){:root{--bg:#15171a;--panel:#1e2125;--ink:#e6e8eb;--muted:#9aa3ad;--line:#33383f;
--bad:#ff8a80;--badbg:#3a1f1d;--warn:#ffd27a;--warnbg:#3a2f14;--ok:#8fd6a5;--okbg:#17301f;--accent:#8ab4f8;--code:#2a2e34;--hit:#6b5300}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.5 system-ui,Segoe UI,sans-serif}
main{max-width:1180px;margin:0 auto;padding:24px 16px 80px}h1{font-size:24px;margin:0 0 4px}h2{font-size:19px;margin:36px 0 8px}
h3{font-size:15px;margin:0 0 10px}code,pre,.log{font-family:ui-monospace,Consolas,monospace;font-size:12.5px}
code{background:var(--code);padding:1px 5px;border-radius:4px;overflow-wrap:anywhere}.muted{color:var(--muted)}.small{font-size:12.5px}
a{color:var(--accent)}.jump{font-size:12.5px;margin-left:6px}
.sessions{width:100%;border-collapse:collapse;background:var(--panel);border:1px solid var(--line);border-radius:8px}
.sessions th,.sessions td{text-align:left;padding:8px 10px;border-bottom:1px solid var(--line);vertical-align:top}
.banner{padding:12px 14px;border-radius:8px;font-weight:600;margin:8px 0 14px;border:1px solid var(--line)}
.bad{border-color:var(--bad)}.banner.bad{background:var(--badbg);color:var(--bad)}.banner.warn{background:var(--warnbg);color:var(--warn)}
.banner.ok{background:var(--okbg);color:var(--ok)}.cards{display:grid;gap:12px}
.card{background:var(--panel);border:1px solid var(--line);border-left-width:4px;border-radius:8px;padding:14px 16px}
.card.bad{border-left-color:var(--bad)}.card.warn{border-left-color:var(--warn)}.card.ok{border-left-color:var(--ok)}
.card hr{border:0;border-top:1px solid var(--line);margin:12px 0}
.facts{border-collapse:collapse;width:100%}.facts th{text-align:left;font-weight:600;color:var(--muted);width:150px;padding:4px 12px 4px 0;vertical-align:top}
.facts td{padding:4px 0;vertical-align:top}.recipes,.frames{margin:4px 0;padding-left:20px}.recipes li{margin-bottom:6px}
.mini{border-collapse:collapse}.mini th,.mini td{border:1px solid var(--line);padding:3px 10px;text-align:left}
.yes{color:var(--ok)}.no{color:var(--bad);font-weight:600}
.chip{display:inline-block;border-radius:10px;padding:0 8px;font-size:12px;font-weight:600}.chip.hit{background:var(--hit)}
.tools{display:flex;flex-wrap:wrap;gap:6px 14px;align-items:center;margin:10px 0;font-size:13px}
.log{width:100%;border-collapse:collapse;background:var(--panel);border:1px solid var(--line)}
.log td{padding:3px 8px;border-bottom:1px solid var(--line);vertical-align:top}.log td:nth-child(1){white-space:nowrap}
.log .rel{white-space:nowrap;color:var(--muted);text-align:right}.log td:last-child{overflow-wrap:anywhere;width:100%}
.side{display:inline-block;width:18px;text-align:center;border-radius:4px;background:var(--code)}
.s-S .side{background:#cfe3ff;color:#123}.s-C .side{background:#d9f2d9;color:#132}
.kind{display:inline-block;min-width:66px;font-weight:600}
.k-ALARM .kind,.k-ERROR .kind{color:var(--bad)}.k-TRACE .kind,.k-CAUSE .kind,.k-ATTRS .kind{color:var(--warn)}.k-MARK .kind{color:var(--accent)}
.k-GUI .kind,.k-EVENT .kind,.k-DIAG .kind,.k-BASELINE .kind,.k-START .kind{color:var(--muted)}
tr.keymoment>td{background:var(--badbg)}tr:target>td{outline:2px solid var(--accent);outline-offset:-2px}
pre{white-space:pre-wrap;margin:6px 0;background:var(--code);padding:8px;border-radius:6px;overflow-wrap:anywhere}
.long{white-space:pre-wrap;margin-top:4px}details>summary{cursor:pointer}.inner{margin-top:6px}
body.keyonly .log>tbody>tr:not(.important):not(.keymoment):not(.fold){display:none}
"""

JS = """
document.querySelectorAll('.keyonly-toggle').forEach(function (box) {
  box.addEventListener('change', function () { document.body.classList.toggle('keyonly', box.checked);
    document.querySelectorAll('.keyonly-toggle').forEach(function (other) { other.checked = box.checked; }); });
});
document.querySelectorAll('.kindfilter').forEach(function (box) {
  box.addEventListener('change', function () {
    var table = document.getElementById(box.dataset.table);
    table.querySelectorAll(':scope > tbody > tr.k-' + box.dataset.kind).forEach(function (row) { row.style.display = box.checked ? '' : 'none'; });
  });
});
if (location.hash) { var el = document.querySelector(location.hash); if (el) { var d = el.closest('details'); while (d) { d.open = true; d = d.parentElement.closest('details'); } el.scrollIntoView(); } }
document.querySelectorAll('a.jump').forEach(function (link) {
  link.addEventListener('click', function () { var el = document.querySelector(link.getAttribute('href'));
    if (!el) return; var d = el.closest('details'); while (d) { d.open = true; d = d.parentElement.closest('details'); } });
});
"""


def render(sessions: list[Session]) -> str:
    out = ["<!doctype html><html lang='en'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>",
           f"<title>CraftWatch report</title><style>{CSS}</style></head><body><main>",
           "<h1>CraftWatch report</h1>",
           f"<p class='muted'>{len(sessions)} log file(s), generated {datetime.now():%Y-%m-%d %H:%M}. "
           "Times are local time of the log. S = server, C = client, – = patch.</p>"]

    out.append("<table class='sessions'><tr><th>Log</th><th>Session</th><th>Mod / game</th><th>Result</th></tr>")
    for number, session in enumerate(sessions, 1):
        level, text = verdict(session)
        span = (f"{session.entries[0].time:%Y-%m-%d} {clock(session.entries[0].time)[:8]} – {clock(session.entries[-1].time)[:8]}"
                if session.entries else "empty")
        out.append(f"<tr><td><a href='#s{number}'>{E(session.path.name)}</a></td><td>{E(span)}</td>"
                   f"<td>{E(session.mod)} / {E(session.game)}</td><td><b style='color:var(--{level})'>{E(text)}</b></td></tr>")
    out.append("</table>")

    for number, session in enumerate(sessions, 1):
        sid = f"s{number}"
        level, text = verdict(session)
        out.append(f"<h2 id='{sid}'>{E(session.path.name)}</h2>")
        out.append(f"<p class='muted small'>{E(str(session.path))} · CraftWatch {E(session.mod)} · game {E(session.game)} · {len(session.entries)} entries</p>")
        if not session.entries:
            out.append("<p>This file contains no CraftWatch log lines.</p>")
            continue
        out.append(f"<div class='banner {level}'>{E(text)}</div>")
        out.append(f"<div class='cards'>{card_cause(session, sid)}{card_damage(session, sid)}{card_symptom(session, sid)}</div>")

        kinds = sorted({entry.kind for entry in session.entries})
        filters = "".join(f"<label><input type='checkbox' class='kindfilter' data-table='{sid}-log' data-kind='{E(kind)}' checked> {E(kind)}</label>"
                          for kind in kinds)
        out.append(f"<h3 style='margin-top:22px'>Log</h3><div class='tools'><label><input type='checkbox' class='keyonly-toggle'> "
                   f"<b>key events only</b></label><span class='muted'>|</span>{filters}</div>")
        out.append(f"<table class='log' id='{sid}-log'><tbody>{timeline(session, sid)}</tbody></table>")

    out.append(f"</main><script>{JS}</script></body></html>")
    return "".join(out)


def collect(arguments: list[str]) -> list[Path]:
    if not arguments:
        arguments = [os.path.join(os.environ.get("APPDATA", "."), "VintagestoryData", "CraftWatch")]
    files: list[Path] = []
    for argument in arguments:
        path = Path(argument)
        if path.is_dir():
            files.extend(sorted(path.glob("*.log")))
        elif path.is_file():
            files.append(path)
        else:
            print(f"not found: {path}", file=sys.stderr)
    return files


def main() -> int:
    parser = argparse.ArgumentParser(description="Render CraftWatch log files as one HTML page.")
    parser.add_argument("logs", nargs="*", help="log files or folders (default: %%APPDATA%%\\VintagestoryData\\CraftWatch)")
    parser.add_argument("-o", "--output", help="HTML file to write (default: craftwatch-report.html next to the first log)")
    parser.add_argument("--open", action="store_true", help="open the page in the default browser")
    options = parser.parse_args()

    files = collect(options.logs)
    if not files:
        print("no log files found", file=sys.stderr)
        return 1

    sessions = [parse(path) for path in files]
    output = Path(options.output) if options.output else files[0].parent / "craftwatch-report.html"
    output.write_text(render(sessions), encoding="utf-8")

    for session in sessions:
        print(f"{session.path.name}: {verdict(session)[1]}")
    print(f"written: {output}")
    if options.open:
        webbrowser.open(output.resolve().as_uri())
    return 0


if __name__ == "__main__":
    sys.exit(main())
