import json
import os
import sys
import time
import urllib.request

BRIDGE = 'http://127.0.0.1:45732/api/download'
HEALTH = 'http://127.0.0.1:45732/api/health'
LOG = os.path.expandvars(r'%LOCALAPPDATA%\WrenchDownloader\startup.log')
HISTORY = os.path.expandvars(r'%LOCALAPPDATA%\WrenchDownloader\history.json')

def bridge_ok():
    try:
        with urllib.request.urlopen(HEALTH, timeout=3) as r:
            return r.status == 200
    except Exception:
        return False

def post_download(payload):
    req = urllib.request.Request(
        BRIDGE, data=json.dumps(payload).encode('utf-8'),
        headers={'Content-Type': 'application/json'}
    )
    with urllib.request.urlopen(req, timeout=10) as r:
        return json.loads(r.read().decode('utf-8'))['id']

def wait_for_completion(track_id, timeout_sec=60):
    deadline = time.time() + timeout_sec
    pos = max(0, os.path.getsize(LOG) - 10000) if os.path.exists(LOG) else 0
    tid8 = track_id[:8]
    while time.time() < deadline:
        if os.path.exists(LOG):
            with open(LOG, 'r', encoding='utf-8', errors='replace') as f:
                f.seek(pos)
                chunk = f.read()
                pos = f.tell()
                for line in chunk.splitlines():
                    if tid8 in line:
                        if 'END OK' in line or 'extension-resolved direct completed' in line:
                            return True, line
                        if 'END FAILED' in line or 'terminal failure' in line:
                            return False, line
        time.sleep(1)
    return False, f"Timeout after {timeout_sec}s"

def verify_file(track_id):
    for _ in range(8):
        if os.path.exists(HISTORY):
            try:
                with open(HISTORY, 'r', encoding='utf-8') as f:
                    hist = json.load(f)
                entry = next((h for h in hist if h.get('Id') == track_id and h.get('SavePath')), None)
                if entry and os.path.exists(entry['SavePath']):
                    path = entry['SavePath']
                    return True, path, os.path.getsize(path)
            except Exception:
                pass
        time.sleep(1)
    return False, None, 0

def test_file_type(label, url, ext, expected_min_bytes=100):
    print(f"\n--- Testing Type: {label} ---")
    payload = {
        'url': url,
        'title': f'Test-FileType-{label}.{ext}',
        'quality': 'file',
        'format': ext,
        'prompt': False,
        'userAgent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36'
    }
    try:
        tid = post_download(payload)
        print(f"Posted {label}, id={tid}")
        ok, detail = wait_for_completion(tid, timeout_sec=45)
        if not ok:
            print(f"FAIL: {label} -> {detail}")
            return False, detail
        
        has_file, path, sz = verify_file(tid)
        if not has_file or sz < expected_min_bytes:
            print(f"FAIL: {label} -> File missing or smaller than {expected_min_bytes} bytes (got {sz})")
            return False, f"File too small ({sz} bytes)"
        
        print(f"PASS: {label} -> {path} ({sz} bytes)")
        return True, path
    except Exception as ex:
        print(f"ERROR: {label} -> {ex}")
        return False, str(ex)

def main():
    if not bridge_ok():
        print("Bridge not running!")
        sys.exit(1)

    suite = [
        # PDF Document (Mozilla PDF.js test PDF)
        ("PDF Document", "https://raw.githubusercontent.com/mozilla/pdf.js/ba2edeae/web/compressed.tracemonkey-pldi-09.pdf", "pdf", 50000),
        
        # GitHub Release Tarball / Archive
        ("GitHub Tarball", "https://raw.githubusercontent.com/git/git/master/Documentation/Makefile", "mk", 500),
        
        # Audio File (W3C Sample Audio)
        ("Audio WAV", "https://www.w3schools.com/html/horse.ogg", "ogg", 1000),
        ("Audio MP3", "https://www.w3schools.com/html/horse.mp3", "mp3", 1000),
        
        # Image Formats
        ("PNG Image", "https://www.google.com/images/branding/googlelogo/2x/googlelogo_color_272x92dp.png", "png", 2000),
        ("WebP Image", "https://www.gstatic.com/webp/gallery/1.webp", "webp", 5000),
        ("SVG Vector", "https://raw.githubusercontent.com/simple-icons/simple-icons/develop/icons/github.svg", "svg", 500),
        
        # Data Files
        ("JSON Data", "https://raw.githubusercontent.com/github/gitignore/main/Python.gitignore", "txt", 100),
    ]

    results = []
    for label, url, ext, min_sz in suite:
        ok, detail = test_file_type(label, url, ext, min_sz)
        results.append((label, ok, detail))

    print("\n" + "="*50)
    print("FILE TYPE TEST RESULTS:")
    all_pass = True
    for label, ok, detail in results:
        st = "PASS" if ok else "FAIL"
        print(f"[{st}] {label}: {detail}")
        if not ok:
            all_pass = False
    print("="*50)
    sys.exit(0 if all_pass else 1)

if __name__ == '__main__':
    main()
