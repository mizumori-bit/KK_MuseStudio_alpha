"""A stand-in for the tailscale CLI in the tests (test_muse_listener.py): `whois --json <ip>` answers from a JSON file
{"<ip>": "<StableID>" | "fail"}, read on each call (a test changes it); `ip -4` prints the file's "_self" entry.
  python fake_tailscale.py <map.json> whois --json <ip>
"""
import json
import sys
from pathlib import Path

table = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
args = sys.argv[2:]
if args[:2] == ["whois", "--json"] and len(args) == 3:
    sid = table.get(args[2].split(":")[0])
    if not sid or sid == "fail":
        sys.stderr.write("no such peer\n")
        sys.exit(1)
    print(json.dumps({"Node": {"ID": 1, "StableID": sid, "Name": "x."}, "UserProfile": {}}))
    sys.exit(0)
if args in (["ip", "-4"], ["ip"]):
    print(table.get("_self", ""))
    sys.exit(0)
sys.exit(2)
