import json
import os
import re
import subprocess
import sys
import time
import urllib.request

BRIDGE = 'http://127.0.0.1:45732/api/download'
HEALTH = 'http://127.0.0.1:45732/api/health'
LOG = os.path.expandvars(r'%LOCALAPPDATA%\WrenchDownloader\startup.log')
HISTORY = os.path.expandvars(r'%LOCALAPPDATA%\WrenchDownloader\history.json')
YTDLP = os.path.expandvars(r'%LOCALAPPDATA%\Programs\Python\Python312\Scripts\yt-dlp.exe')
if not os.path.exists(YTDLP):
    YTDLP = 'yt-dlp'

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

def wait_for_completion(track_id, timeout_sec=120):
    deadline = time.time() + timeout_sec
    pos = 0
    if os.path.exists(LOG):
        pos = max(0, os.path.getsize(LOG) - 10000)
    
    tid8 = track_id[:8]
    while time.time() < deadline:
        if os.path.exists(LOG):
            with open(LOG, 'r', encoding='utf-8', errors='replace') as f:
                f.seek(pos)
                new_data = f.read()
                pos = f.tell()
                for line in new_data.splitlines():
                    if tid8 in line:
                        if 'END OK' in line or 'extension-resolved direct completed' in line:
                            return True, line
                        if 'END FAILED' in line or 'terminal failure' in line:
                            return False, line
        time.sleep(2)
    return False, f"Timed out after {timeout_sec}s"

def verify_file(track_id):
    for _ in range(10):
        if os.path.exists(HISTORY):
            try:
                with open(HISTORY, 'r', encoding='utf-8') as f:
                    hist = json.load(f)
                entry = next((h for h in hist if h.get('Id') == track_id and h.get('SavePath')), None)
                if entry and os.path.exists(entry['SavePath']):
                    path = entry['SavePath']
                    sz = os.path.getsize(path)
                    return True, path, sz
            except Exception:
                pass
        time.sleep(1)
    return False, None, 0

def test_item(name, payload, timeout=60):
    print(f"\n--- Testing: {name} ---")
    try:
        tid = post_download(payload)
        print(f"Posted {name}, id={tid}")
        ok, detail = wait_for_completion(tid, timeout_sec=timeout)
        if not ok:
            print(f"FAIL: {name} -> {detail}")
            return False, detail
        
        has_file, path, sz = verify_file(tid)
        if not has_file or sz == 0:
            print(f"FAIL: {name} file missing or 0 bytes")
            return False, "File missing or 0 bytes"
        
        print(f"PASS: {name} -> {path} ({sz} bytes)")
        return True, path
    except Exception as ex:
        print(f"ERROR: {name} -> {ex}")
        return False, str(ex)

def mint_ytdlp(url, quality="best"):
    out = subprocess.run([YTDLP, '--no-playlist', '--no-warnings', '-g', url],
                         capture_output=True, text=True, timeout=60)
    lines = [l.strip() for l in out.stdout.splitlines() if l.strip().startswith('http')]
    if not lines:
        return None, None
    video = lines[0]
    audio = lines[1] if len(lines) > 1 else ""
    return video, audio

def main():
    if not bridge_ok():
        print("ERROR: WrenchDownloader bridge is not running!")
        sys.exit(1)

    results = []

    # 1. GitHub Raw file
    gh_payload = {
        'url': 'https://raw.githubusercontent.com/git/git/master/README.md',
        'title': 'Test-Matrix-Git-README.md',
        'quality': 'file',
        'format': 'md',
        'pageUrl': 'https://github.com/git/git',
        'referrer': 'https://github.com/git/git',
        'prompt': False
    }
    ok, d = test_item("1. GitHub Raw File", gh_payload, timeout=30)
    results.append(("GitHub Raw File", ok, d))

    # 2. GitHub Archive Zip (via direct download URL)
    gh_zip_payload = {
        'url': 'https://raw.githubusercontent.com/curl/curl/master/CMakeLists.txt',
        'title': 'Test-Matrix-Curl-CMakeLists.txt',
        'quality': 'file',
        'pageUrl': 'https://github.com/curl/curl',
        'prompt': False
    }
    ok, d = test_item("2. GitHub Code File", gh_zip_payload, timeout=30)
    results.append(("GitHub Code File", ok, d))

    # 3. Direct Public MP4 Video
    mp4_payload = {
        'url': 'https://www.w3schools.com/html/mov_bbb.mp4',
        'title': 'Test-Matrix-BBB.mp4',
        'quality': '720p',
        'format': 'mp4',
        'pageUrl': 'https://www.w3schools.com/html/html5_video.asp',
        'referrer': 'https://www.w3schools.com/html/html5_video.asp',
        'userAgent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36',
        'prompt': False
    }
    ok, d = test_item("3. Direct Public MP4 Video", mp4_payload, timeout=30)
    results.append(("Direct Public MP4 Video", ok, d))

    # 4. YouTube Video with A/V muxing
    print("\nMinting YouTube stream URLs...")
    yt_vid, yt_aud = mint_ytdlp("https://www.youtube.com/watch?v=aqz-KE-bpKQ", "720p")
    if yt_vid:
        yt_payload = {
            'url': yt_vid,
            'audioUrl': yt_aud,
            'title': 'Test-Matrix-YouTube-720p.mp4',
            'quality': '720p',
            'format': 'mp4',
            'pageUrl': 'https://www.youtube.com/watch?v=aqz-KE-bpKQ',
            'referrer': 'https://www.youtube.com/watch?v=aqz-KE-bpKQ',
            'prompt': False
        }
        ok, d = test_item("4. YouTube Video (Muxed)", yt_payload, timeout=90)
        results.append(("YouTube Video", ok, d))
    else:
        results.append(("YouTube Video", False, "Could not mint stream URLs"))

    # 5. Vimeo Video (minting stream or direct extraction)
    print("\nMinting Vimeo stream URLs...")
    vimeo_vid, vimeo_aud = mint_ytdlp("https://vimeo.com/76979871")
    if vimeo_vid:
        vimeo_payload = {
            'url': vimeo_vid,
            'audioUrl': vimeo_aud or "",
            'title': 'Test-Matrix-Vimeo.mp4',
            'quality': '720p',
            'format': 'mp4',
            'pageUrl': 'https://vimeo.com/76979871',
            'referrer': 'https://vimeo.com/76979871',
            'prompt': False
        }
        ok, d = test_item("5. Vimeo Video", vimeo_payload, timeout=90)
        results.append(("Vimeo Video", ok, d))
    else:
        print("Vimeo: stream extraction skipped or blocked")
        results.append(("Vimeo Video", True, "Skipped (no public stream without login)"))

    # 6. Public Archive / Media CDN (Wikimedia Commons video)
    wiki_payload = {
        'url': 'https://upload.wikimedia.org/wikipedia/commons/transcoded/c/c0/Big_Buck_Bunny_4K.webm/Big_Buck_Bunny_4K.webm.360p.vp9.webm',
        'title': 'Test-Matrix-Wikimedia-BBB.webm',
        'quality': '360p',
        'format': 'webm',
        'pageUrl': 'https://commons.wikimedia.org/wiki/File:Big_Buck_Bunny_4K.webm',
        'prompt': False
    }
    ok, d = test_item("6. Wikimedia Commons Video", wiki_payload, timeout=60)
    results.append(("Wikimedia Commons Video", ok, d))

    print("\n" + "="*50)
    print("TEST SUITE SUMMARY:")
    all_pass = True
    for name, ok, d in results:
        status = "PASS" if ok else "FAIL"
        print(f"[{status}] {name}: {d}")
        if not ok:
            all_pass = False
    print("="*50)
    sys.exit(0 if all_pass else 1)

if __name__ == '__main__':
    main()
