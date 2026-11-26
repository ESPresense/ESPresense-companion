#!/usr/bin/env python3
"""Recomputes room-flip-2220.json's "expect" block from its own records and diffs the two.

The point of this script is that the pinned numbers in "expect" are checkable without a .NET
toolchain, and that whoever writes the C# test has a reference oracle to match. It reads only the
fixture - no network, no companion build.

    python3 verify_expectations.py [path/to/room-flip-2220.json]

Exit 0 when every pinned number reproduces, 1 otherwise.
"""
import collections
import json
import os
import statistics
import sys


def in_polygon(x, y, poly):
    """Ray cast, half-open on the upper edge."""
    inside = False
    n = len(poly)
    for i in range(n):
        x1, y1 = poly[i]
        x2, y2 = poly[(i + 1) % n]
        if (y1 > y) != (y2 > y):
            if x < (x2 - x1) * (y - y1) / (y2 - y1) + x1:
                inside = not inside
    return inside


def solve_room(fx, x, y, z):
    """(floor name, room name) containing a point, from the fixture's own geometry."""
    for fid, bounds in fx["floorBounds"].items():
        if bounds[0][2] <= z < bounds[1][2]:
            for room in fx["rooms"]:
                if room["floor"] == fid and in_polygon(x, y, room["points"]):
                    return fx["floorNames"][fid], room["id"]
            return fx["floorNames"][fid], None
    return None, None


def recompute(fx):
    floor_of_room = {r["id"]: fx["floorNames"][r["floor"]] for r in fx["rooms"]}
    by_beacon = collections.defaultdict(list)
    for r in fx["reports"]:
        by_beacon[r["beacon"]].append(r)

    out = {"consistency": {}, "flips": {}, "solvedRoom": {}, "windows": {}}
    for label, rs in by_beacon.items():
        agree = on_floor = 0
        solved = collections.Counter()
        for r in rs:
            _floor, room = solve_room(fx, r["x"], r["y"], r["z"])
            solved[room or "<floor-only>"] += 1
            agree += r["state"] == room
            on_floor += floor_of_room.get(r["state"]) == r["bestScenario"]
        states = [r["state"] for r in rs]
        out["consistency"][label] = {
            "reports": len(rs),
            "stateEqualsSolvedRoom": agree,
            "stateOnBestScenarioFloor": on_floor,
        }
        out["flips"][label] = {
            "distinctStates": sorted(set(states)),
            "stateChanges": sum(1 for i in range(1, len(states)) if states[i] != states[i - 1]),
        }
        out["solvedRoom"][label] = dict(solved.most_common())
        out["windows"][label] = {
            "tFirst": rs[0]["t"],
            "tLast": rs[-1]["t"],
            "sdX": round(statistics.pstdev([r["x"] for r in rs]), 3),
            "sdY": round(statistics.pstdev([r["y"] for r in rs]), 3),
            "sdZ": round(statistics.pstdev([r["z"] for r in rs]), 3),
        }
    return out


def main(argv):
    path = argv[1] if len(argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                                      "room-flip-2220.json")
    with open(path) as f:
        fx = json.load(f)

    got = recompute(fx)
    want = fx["expect"]
    failures = []
    for section in sorted(want):
        for label in sorted(want[section]):
            w, g = want[section][label], got.get(section, {}).get(label)
            if w != g:
                failures.append(f"  expect.{section}.{label}\n    pinned:      {w}\n    recomputed:  {g}")

    for label in sorted(want["consistency"]):
        c = want["consistency"][label]
        pct = 100.0 * c["stateEqualsSolvedRoom"] / c["reports"]
        print(f'{label:7s} {c["reports"]:5d} reports  '
              f'state == room containing its own x/y/z: {c["stateEqualsSolvedRoom"]:5d} ({pct:5.1f}%)  '
              f'room changes: {want["flips"][label]["stateChanges"]:4d}')

    if failures:
        print(f"\nFAIL: {len(failures)} pinned value(s) did not reproduce:\n" + "\n".join(failures))
        return 1
    print(f"\nOK: every pinned value in expect reproduces from reports[] + the fixture geometry.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
