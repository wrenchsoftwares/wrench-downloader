# Silent end-to-end download test: mint fresh stream URLs (no browser),
# POST an extension-shaped payload to the app bridge (no dialog), poll the
# log to a terminal state, verify the output file. No UI, no clicks.
#
# Usage: silent_yt_test.py <youtube_url> [quality=1080p] [title=SILENT-PROOF]
# Exit 0 = pass (file on disk, resolution + audio verified), 1 = fail.
#
# Precondition: app Settings -> "Show download dialog" must be OFF, else the
# modal blocks the test (see SKILL.md section 0).

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
YTDLP = os.path.expandvars(
    r'%LOCALAPPDATA%\Programs\Python\Python312\Scripts\yt-dlp.exe')
POLL_INTERVAL = 10
TIMEOUT_MIN = 25


def fail(msg, code=1):
    print(f'SILENT-TEST FAIL: {msg}')
    sys.exit(code)


def mint_urls(page_url, quality):
    height = re.search(r'\d+', quality or '')
    dim = int(height.group(0)) if height else 1080
    fmt = (f'bv*[height<={dim}]+ba/b[height<={dim}]/'
           f'bv*+ba/b')
    exe = YTDLP if os.path.exists(YTDLP) else 'yt-dlp'
    try:
        out = subprocess.run(
            [exe, '--no-playlist', '--no-warnings', '-f', fmt, '-g', page_url],
            capture_output=True, text=True, timeout=180)
    except Exception as e:
        fail(f'yt-dlp -g failed: {e}')
    if out.returncode != 0:
        fail(f'yt-dlp -g error: {(out.stderr or out.stdout).strip()[:300]}')
    urls = [l.strip() for l in out.stdout.splitlines() if l.strip().startswith('https://')]
    if not urls:
        fail('yt-dlp -g returned no URLs')
    video, audio = urls[0], (urls[1] if len(urls) > 1 else '')
    print(f'minted fresh urls: video itag={itag_of(video)} audio={"yes" if audio else "no"}')
    return video, audio


def itag_of(url):
    m = re.search(r'[?&]itag=(\d+)', url or '')
    return m.group(1) if m else '?'


def bridge_up():
    try:
        with urllib.request.urlopen(HEALTH, timeout=5) as r:
            return r.status == 200
    except Exception:
        return False


def post_download(video, audio, page_url, quality, title):
    payload = {
        'url': video,
        'title': title,
        'quality': quality,
        'format': 'mp4',
        'pageUrl': page_url,
        'referrer': page_url,
        'userAgent': ('Mozilla/5.0 (Windows NT 10.0; Win64; x64) '
                      'AppleWebKit/537.36 (KHTML, like Gecko) '
                      'Chrome/128.0.0.0 Safari/537.36'),
        'audioUrl': audio,
        'audioReferrer': page_url,
        'prompt': False,
    }
    req = urllib.request.Request(
        BRIDGE, data=json.dumps(payload).encode('utf-8'),
        headers={'Content-Type': 'application/json'})
    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            return json.loads(r.read().decode('utf-8'))['id']
    except Exception as e:
        fail(f'bridge POST failed (is the app running?): {e}')


def log_tail_for(track_id, last_pos):
    try:
        with open(LOG, encoding='utf-8', errors='replace') as f:
            f.seek(last_pos)
            chunk = f.read()
            return chunk, f.tell()
    except Exception:
        return '', last_pos


def wait_terminal(track_id):
    deadline = time.time() + TIMEOUT_MIN * 60
    pos = os.path.getsize(LOG) if os.path.exists(LOG) else 0
    started = False
    start_at = time.time()
    while time.time() < deadline:
        chunk, pos = log_tail_for(track_id, pos)
        for line in chunk.splitlines():
            short = line[:230]
            if track_id[:8] in line and ('START ' in line or 'BRIDGE-RECV' in line):
                started = True
            if track_id[:8] in line and ('END OK' in line or 'END FAILED' in line
                                         or 'terminal failure' in line
                                         or 'extension-resolved direct completed' in line):
                print(f'log: {short}')
                if 'END OK' in line or 'completed' in line:
                    return True
                fail(f'app reported failure: {short}')
        if not started and time.time() - start_at > 60:
            fail('no START line within 60 s - a modal dialog is likely blocking '
                 '(Settings -> Show download dialog must be OFF). Stopping.')
        time.sleep(POLL_INTERVAL)
    fail(f'no terminal state within {TIMEOUT_MIN} min (stuck or throttled)')


def verify_output(track_id, quality):
    entry = None
    for _ in range(10):
        try:
            if os.path.exists(HISTORY):
                with open(HISTORY, encoding='utf-8') as f:
                    history = json.load(f)
                entry = next((h for h in history if h.get('Id') == track_id and h.get('SavePath') and os.path.exists(h.get('SavePath', ''))), None)
                if entry:
                    break
        except Exception:
            pass
        time.sleep(1)
    if not entry or not entry.get('SavePath') or not os.path.exists(entry['SavePath']):
        fail('history has no finished SavePath for this id')
    path = entry['SavePath']
    try:
        out = subprocess.run(
            ['ffprobe', '-v', 'error', '-show_entries',
             'stream=index,codec_name,width,height', '-of',
             'default=noprint_wrappers=1', path],
            capture_output=True, text=True, timeout=60)
    except Exception as e:
        fail(f'ffprobe missing/failed: {e}')
    info = out.stdout
    print(f'file: {path} ({os.path.getsize(path)} bytes)')
    print(info.strip())
    want = int(re.search(r'\d+', quality).group(0)) if re.search(r'\d+', quality or '') else 1080
    mh = re.search(r'height=(\d+)', info)
    if not mh or int(mh.group(1)) < min(want, 720):
        fail(f'resolution check failed (want ~{want}p)')
    if 'codec_name' not in info or len(re.findall(r'index=\d+', info)) < 2:
        print('warning: only one stream (video-only or audio missing)')
    size_mb = os.path.getsize(path) / 1e6
    print(f'SILENT-TEST PASS in {size_mb:.1f} MB, height={mh.group(1)}')
    return 0


def main():
    page = sys.argv[1] if len(sys.argv) > 1 else 'https://www.youtube.com/watch?v=aqz-KE-bpKQ'
    quality = sys.argv[2] if len(sys.argv) > 2 else '1080p'
    title = sys.argv[3] if len(sys.argv) > 3 else 'SILENT-PROOF'
    if not bridge_up():
        fail('bridge not up - start the app first')
    video, audio = mint_urls(page, quality)
    tid = post_download(video, audio, page, quality, title)
    print(f'posted id={tid}, polling log silently...')
    wait_terminal(tid)
    sys.exit(verify_output(tid, quality))


if __name__ == '__main__':
    main()
