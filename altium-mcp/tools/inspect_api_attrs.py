"""Fetch C25744 from the EasyEDA product search endpoint and print attribute keys
to confirm which key carries the LCSC part number."""
from __future__ import annotations

import json
import urllib.parse
import urllib.request

LCSC = "C25744"
URL = "https://pro.easyeda.com/api/v2/devices/search"
UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36"

form = urllib.parse.urlencode({
    "page": "1",
    "pageSize": "1",
    "wd": LCSC.lower(),
    "returnListStyle": "classifyarr",
}).encode()

req = urllib.request.Request(URL, data=form, headers={
    "User-Agent": UA,
    "Content-Type": "application/x-www-form-urlencoded",
    "Referer": "https://pro.easyeda.com/editor",
    "Origin": "https://pro.easyeda.com",
})
with urllib.request.urlopen(req, timeout=30) as r:
    data = json.loads(r.read().decode("utf-8"))

try:
    info = data["result"]["lists"]["lcsc"][0]
    attrs = info.get("attributes", {})
    print(f"Part: {info.get('lcsc', {})}")
    print(f"\nAttribute keys ({len(attrs)}):")
    for k, v in attrs.items():
        vsafe = (v or "").encode("ascii", "replace").decode("ascii")
        print(f"  {k!r} = {vsafe!r}")
except (KeyError, IndexError) as exc:
    print("Could not parse result:", exc)
    print(json.dumps(data, indent=2)[:2000])
