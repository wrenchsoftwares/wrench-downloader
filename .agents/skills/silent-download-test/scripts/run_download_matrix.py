"""Generic silent download matrix. Usage: python run_download_matrix.py <config.json>

All site-specific values (URLs, titles, ids) live in the config file, never here.
"""
import json
import os
import subprocess
import sys
import time
import urllib.request

BRIDGE = 'http://127.0.0.1:45732/api/download'
HEALTH = 'http://127.0.0.1:45732/api/health'
LOG = os.environ.get('WRENCH_LOG', os.path.expandvars(r'%LOCALAPPDATA%\WrenchDownloader\startup.log'))
HISTORY = os.environ.get('WRENCH_HISTORY', os.path.expandvars(r'%LOCALAPPDATA%\WrenchDownloader\history.json'))
YTDLP = os.path.expandvars(r'%LOCALAPPDATA%\Programs\Python\Python312\Scripts\yt-dlp.exe')
if not os.path.exists(YTDLP):
    YTDLP = 'yt-dlp'


def bridge_ok():
    try:
        with urllib.request.urlopen(HEALTH, timeout=3) as r:
            return r.status == 200
    except Exception:
        return False


def post(payload):
    req = urllib.request.Request(
        BRIDGE,
        data=json.dumps(payload).encode('utf-8'),
        headers={'Content-Type': 'application/json'},
    )
    with urllib.request.urlopen(req, timeout=10) as r:
        return json.loads(r.read().decode('utf-8'))['id']


def check_history(track_id):
    """Return (status, detail) from history.json: pending|completed|failed."""
    if not os.path.exists(HISTORY):
        return 'pending', ''
    try:
        with open(HISTORY, 'r', encoding='utf-8') as f:
            hist = json.load(f)
    except Exception:
        return 'pending', ''
    entry = next((h for h in hist if h.get('Id') == track_id), None)
    if not entry:
        return 'pending', ''
    status = str(entry.get('Status', '')).lower()
    if status in ('completed', 'complete', 'done'):
        path = entry.get('SavePath', '')
        if path and os.path.exists(path):
            return 'completed', (path, os.path.getsize(path))
        return 'completed', ('', 0)
    if status in ('failed', 'error'):
        return 'failed', entry.get('StatusText', 'unknown error')
    return 'pending', entry.get('StatusText', '')


def mint(page_url):
    out = subprocess.run(
        [YTDLP, '--no-playlist', '--no-warnings', '-g', page_url],
        capture_output=True, text=True, timeout=120,
    )
    lines = [l.strip() for l in out.stdout.splitlines() if l.strip().startswith('http')]
    if not lines:
        return None, None, out.stderr.strip()[:300]
    return lines[0], (lines[1] if len(lines) > 1 else ''), None


def run_case(case):
    label = case['label']
    print('\n--- %s ---' % label, flush=True)
    payload = dict(case.get('payload', {}))
    if case.get('ytdlp_page_url'):
        try:
            video, audio, err = mint(case['ytdlp_page_url'])
        except Exception as ex:
            video, audio, err = None, None, str(ex)
        if not video:
            print('SKIP: %s (no stream minted: %s)' % (label, err), flush=True)
            return 'SKIP', 'no stream'
        payload['url'] = video
        if audio:
            payload['audioUrl'] = audio
    payload['prompt'] = False
    try:
        tid = post(payload)
    except Exception as ex:
        print('FAIL: %s (post: %s)' % (label, ex), flush=True)
        return 'FAIL', str(ex)
    print('Posted %s id=%s' % (label, tid), flush=True)
    deadline = time.time() + case.get('timeout', 60)
    while time.time() < deadline:
        state, detail = check_history(tid)
        if state == 'failed':
            print('FAIL: %s -> %s' % (label, detail), flush=True)
            return 'FAIL', detail
        if state == 'completed':
            path, size = detail
            if path and size >= case.get('min_bytes', 1):
                print('PASS: %s -> %s (%s bytes)' % (label, path, size), flush=True)
                return 'PASS', '%s (%s bytes)' % (path, size)
            print('FAIL: %s -> missing/small file (%s bytes)' % (label, size), flush=True)
            return 'FAIL', 'file missing or < %s bytes' % case.get('min_bytes', 1)
        time.sleep(2)
    print('FAIL: %s -> no terminal status within %ss' % (label, case.get('timeout', 60)), flush=True)
    return 'FAIL', 'timeout waiting for history status'


def main():
    if len(sys.argv) != 2:
        print('Usage: run_download_matrix.py <config.json>')
        sys.exit(2)
    with open(sys.argv[1], 'r', encoding='utf-8') as f:
        cfg = json.load(f)
    if not bridge_ok():
        print('ERROR: bridge not running')
        sys.exit(1)
    results = []
    for case in cfg['cases']:
        status, detail = run_case(case)
        results.append((case['label'], status, detail))
    print('\n' + '=' * 60)
    counts = {'PASS': 0, 'FAIL': 0, 'SKIP': 0}
    for label, status, detail in results:
        counts[status] += 1
        print('[%s] %s: %s' % (status, label, detail))
    print('Totals: %s' % counts)
    print('=' * 60)
    sys.exit(1 if counts['FAIL'] else 0)


if __name__ == '__main__':
    main()
