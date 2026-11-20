"""Named checkpoints for Selenium tests.

Use `check("what is true", condition, "detail if it is not")` instead of a bare `assert`. Each checkpoint:
  1. takes a Selenium screenshot at that moment (results/screenshots/<test>__NN.png),
  2. appends itself to results/checkpoints.json (test, step, name, outcome, message, screenshot),
  3. then asserts, so the test passes or fails exactly as a normal assert would.

A test can declare its checkpoints up front with `check.plan([...])`; any it never reaches (because an earlier
one failed) are recorded as "not reached", so a report shows the whole intended flow, not just the part that ran.
The JSON format is the same in every language of this suite, so one reader (the UiPath test case) handles all.
"""
import json
import re
import time
from pathlib import Path

RESULTS = Path(__file__).parent / "results"
SHOTS = RESULTS / "screenshots"
LOG = RESULTS / "checkpoints.json"
TRAIL = RESULTS / "execution-trail.log"


def _load():
    try:
        return json.loads(LOG.read_text(encoding="utf-8"))
    except (FileNotFoundError, ValueError):
        return []


def _save(entries):
    RESULTS.mkdir(parents=True, exist_ok=True)
    LOG.write_text(json.dumps(entries, indent=2), encoding="utf-8")


class Checkpoints:
    def __init__(self, driver, test_name):
        self.driver = driver
        self.test = test_name
        self.safe = re.sub(r"[^A-Za-z0-9_-]", "_", test_name)
        self.step = 0
        self.planned = []

    def plan(self, names):
        self.planned = list(names)

    def __call__(self, name, condition, detail=""):
        self.step += 1
        passed = bool(condition)
        SHOTS.mkdir(parents=True, exist_ok=True)
        slug = re.sub(r"[^A-Za-z0-9]+", "_", name).strip("_")[:60]
        shot = f"{self.safe}__{self.step:02d}_{slug}.png"
        try:
            self.driver.save_screenshot(str(SHOTS / shot))
        except Exception:          # a dead browser must not hide the real result
            shot = ""
        try:
            url, title = self.driver.current_url, self.driver.title
        except Exception:
            url, title = "", ""
        outcome = "passed" if passed else "failed"
        stamp = time.strftime("%Y-%m-%dT%H:%M:%S")
        entries = _load()
        entries.append({
            "test": self.test, "step": self.step, "name": name,
            "outcome": outcome,
            "message": "" if passed else (detail or name),
            "screenshot": f"screenshots/{shot}" if shot else "",
            "url": url, "title": title,
            "time": stamp,
        })
        _save(entries)
        trail(f"{self.test} | checkpoint {self.step} {outcome.upper()}: {name}"
              + ("" if passed else f" | {detail}") + f" | {url}")
        if not passed:
            raise AssertionError(f"Checkpoint {self.step} failed: {name}. {detail}".strip())
        return True

    def close(self):
        """Record planned checkpoints that were never reached."""
        reached = self.step
        if len(self.planned) > reached:
            entries = _load()
            for i, name in enumerate(self.planned[reached:], start=reached + 1):
                entries.append({"test": self.test, "step": i, "name": name, "outcome": "not reached",
                                "message": "Not run: an earlier checkpoint failed", "screenshot": "",
                                "url": "", "title": "", "time": time.strftime("%Y-%m-%dT%H:%M:%S")})
                trail(f"{self.test} | checkpoint {i} NOT REACHED: {name}")
            _save(entries)


def trail(line):
    """Plain-text, timestamped execution trail: results/execution-trail.log."""
    RESULTS.mkdir(parents=True, exist_ok=True)
    with TRAIL.open("a", encoding="utf-8") as f:
        f.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')}  {line}\n")


def reset():
    """Start each run with an empty checkpoint log and trail."""
    for path in (LOG, TRAIL):
        if path.exists():
            path.unlink()
