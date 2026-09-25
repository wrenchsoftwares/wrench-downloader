# Sends a test download payload to the local Wrench Downloader extension bridge
# Usage: python test_bridge.py [url] [filename]

import sys
import json
import urllib.request

url = sys.argv[1] if len(sys.argv) > 1 else "https://speed.hetzner.de/100MB.bin"
title = sys.argv[2] if len(sys.argv) > 2 else "100MB_Test_File.bin"

payload = {
    "url": url,
    "title": title,
    "quality": "file",
    "format": "bin",
    "pageUrl": "https://test.local",
    "referrer": "https://test.local",
    "userAgent": "WrenchTester/1.0",
    "prompt": False
}

BRIDGE_URL = "http://127.0.0.1:45732/api/download"

try:
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(BRIDGE_URL, data=data, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=3.0) as resp:
        result = json.loads(resp.read().decode("utf-8"))
        print(f"[+] Download successfully sent to Wrench Downloader! Response: {result}")
except Exception as e:
    print(f"[-] Failed to send to bridge ({BRIDGE_URL}): {e}")
    sys.exit(1)
