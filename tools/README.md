# craftwatch_report.py

Turns the log files of the [CraftWatch](../README.md) mod into one HTML page. A log can contain
several incidents: each starts with a write to the shared static attributes and ends with a repair
(`/cw repair`) or with the end of the log. For every incident the page is made to answer three
questions quickly:

1. **When** did the bug occur?
2. **Which recipe** was being crafted and no longer works?
3. **Which handbook recipe** probably caused it?

## Requirements

Python 3.9 or newer. Nothing else: the script uses only the standard library. It was written and
tested with Python 3.14 on Windows.

The script is a single file. It is in this folder and is also attached to the
[releases](https://github.com/ChristianJog/vs-craftwatch/releases/latest) of the mod.

## Usage

    python craftwatch_report.py [LOG_OR_DIR ...] [-o OUTPUT] [--open]

| Argument | Meaning |
|---|---|
| `LOG_OR_DIR` | one or more log files or folders. A folder stands for all `*.log` files in it. Default: `%APPDATA%\VintagestoryData\CraftWatch` |
| `-o OUTPUT` | the HTML file to write. Default: `craftwatch-report.html` next to the first log |
| `--open` | open the page in the default browser when done |

Examples:

    python craftwatch_report.py --open
    python craftwatch_report.py "C:\some\folder\craftwatch-2026-10-10_12-50-18.log" -o report.html

All given logs end up on the same page. The script prints one result line per log and the path of
the page it wrote. It only reads the logs and writes that one HTML file.

## What the page shows

At the top there is a table of all logs with their result and, if a log has more than one, its
incidents. For every incident follow three boxes, and for every log the log itself.

**1 · Cause** — the write to the shared static attributes:

- the time of the write
- since when the handbook was open, and which page it showed
- the item that was being rendered
- the attribute that was written, and the spoilage data in it
- the call stack, reduced to the frames of the game
- the probable recipe: the barrel recipes that use the rendered item as an ingredient. A recipe
  whose output or ingredients are named by the open handbook page is marked.

**2 · Damage** — the ingredients the game no longer recognises:

- per side (server, client) the time and the numbers: ingredients no longer accepted by the fast
  path, stacks that carry foreign attributes, affected recipe references, a few examples
- if the incident was repaired: when, by whom, how many stacks were re-attached, and the first
  craft after it for which the server filled the output slot again
- otherwise whether the state recovered before the log ended

**3 · Failed craft** — the recipe that matches while the output slot stays empty:

- the time, also relative to the write
- the recipe, the ingredients it needs, the content of the grid and the output slot
- per ingredient whether server and client still accept it

**Log** — every entry with its time, the time relative to the write of its incident, side and
kind. The moments above and the repairs are highlighted, labelled with the number of their
incident, and can be reached with the "show in log" links. "key events only"
hides everything but the entries that matter; the other checkboxes hide single kinds. Long runs of
similar lines and long messages are folded and open on a click.

## How the result is decided

| Result | Condition |
|---|---|
| Bug occurred | an incident contains an `ALARM SIGNATURE`: a recipe matched and the output slot stayed empty. The server's view is preferred over the client's. |
| Corruption, no failed craft recorded | there is a write to the static field or broken ingredients, but no craft was attempted before the incident ended |
| No bug in this log | neither of the above |

Incidents are separated by the `REPAIR` lines of the server. Within an incident the cause is the
first `TRACE` line that reports a write to `defaultEmptyAttributes`; further writes are listed.
The damage is the first report at or after that time in which the fast path rejects at least one
ingredient.

## Limits

- The page shows what the log contains. Item, handbook page and candidate recipes are written by
  CraftWatch 0.3.0 and newer; for logs of older versions the page says that they were not recorded.
- "Probable recipe" is an inference: it lists the barrel recipes that use the rendered item. Which
  of them the handbook actually drew is not in the log.
- The page is self-contained (no network access), but a log with many entries makes it large.
