# Inspect recent downloads, error logs, and bridge health locally
# Returns a lean summary to optimize LLM tokens (per Global Guidelines)

import os
import json
import urllib.request
from datetime import datetime

LOCALAPPDATA = os.environ.get("LOCALAPPDATA", "")
HISTORY_PATH = os.path.join(LOCALAPPDATA, "WrenchDownloader", "history.json")
STARTUP_LOG = os.path.join(LOCALAPPDATA, "WrenchDownloader", "startup.log")
BRIDGE_HEALTH_URL = "http://127.0.0.1:45732/api/health"

print("==================================================")
print("       WRENCH DOWNLOADER HEALTH & STATUS          ")
print("==================================================")

# 1. Bridge Server Check
try:
    req = urllib.request.Request(BRIDGE_HEALTH_URL)
    with urllib.request.urlopen(req, timeout=1.5) as resp:
        if resp.status == 200:
            print("[+] Desktop Bridge Server: ONLINE (port 45732)")
        else:
            print(f"[-] Desktop Bridge Server: Responded with status {resp.status}")
except Exception as e:
    print(f"[-] Desktop Bridge Server: OFFLINE or Not Running ({e})")

# 2. History Summary
if os.path.exists(HISTORY_PATH):
    try:
        with open(HISTORY_PATH, "r", encoding="utf-8") as f:
            data = json.load(f)
        total = len(data)
        completed = sum(1 for d in data if d.get("Status") == 2 or d.get("StatusText") == "Completed")
        failed = sum(1 for d in data if d.get("Status") == 4 or "failed" in str(d.get("StatusText", "")).lower())
        downloading = sum(1 for d in data if d.get("Status") == 1 or d.get("StatusText") == "Downloading")
        
        print(f"\n[+] History ({HISTORY_PATH}):")
        print(f"    Total items: {total} | Completed: {completed} | Failed: {failed} | Downloading: {downloading}")
        
        print("\n--- Last 5 Items ---")
        for item in data[:5]:
            title = item.get("Title", "Unknown")[:45]
            status = item.get("StatusText", "Unknown")
            size = item.get("SizeText", "")
            print(f"  • [{status}] {title} {size}")
    except Exception as ex:
        print(f"[-] Failed to read history.json: {ex}")
else:
    print(f"\n[-] No history.json found at {HISTORY_PATH}")

# 3. Startup Log Check
if os.path.exists(STARTUP_LOG):
    try:
        with open(STARTUP_LOG, "r", encoding="utf-8", errors="ignore") as f:
            lines = f.readlines()
        recent = [l.strip() for l in lines[-10:] if l.strip()]
        if recent:
            print("\n--- Recent Logs ---")
            for l in recent:
                print(f"  {l}")
    except Exception as ex:
        print(f"[-] Failed to read startup.log: {ex}")

print("==================================================")
