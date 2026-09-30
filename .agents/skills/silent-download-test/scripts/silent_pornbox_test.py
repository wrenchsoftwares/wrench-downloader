# Silent end-to-end download test for Pornbox 1080p streams:
# Mints fresh 1080p stream URLs from Pornbox API (mimicking companion extension DOM sniff),
# POSTs an extension-shaped payload to the app bridge (no dialog),
# polls startup.log to a terminal state (END OK), and verifies the output with ffprobe.
#
# Usage: python silent_pornbox_test.py [video_id=216006] [quality=1080p]

import json
import os
import re
import ssl
import subprocess
import sys
import time
import urllib.request

BRIDGE = 'http://127.0.0.1:45732/api/download'
HEALTH = 'http://127.0.0.1:45732/api/health'
LOG = os.path.expandvars(r'%LOCALAPPDATA%\WrenchDownloader\startup.log')
HISTORY = os.path.expandvars(r'%LOCALAPPDATA%\WrenchDownloader\history.json')
POLL_INTERVAL = 2
TIMEOUT_SEC = 180

def fail(msg, code=1):
    print(f'PORNBOX-TEST FAIL: {msg}')
    sys.exit(code)

def bridge_up():
    try:
        with urllib.request.urlopen(HEALTH, timeout=5) as r:
            return r.status == 200
    except Exception:
        return False

def get_pornbox_stream(video_id, target_quality='1080p'):
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE

    headers = {
        'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36',
        'Accept': 'application/json, text/plain, */*',
        'Referer': f'https://pornbox.com/application/watch-page/{video_id}',
    }

    # 1. Fetch content metadata
    content_url = f'https://pornbox.com/contents/{video_id}'
    req = urllib.request.Request(content_url, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=10, context=ctx) as resp:
            content_data = json.loads(resp.read().decode('utf-8', errors='ignore'))
    except Exception as e:
        fail(f'Failed to fetch Pornbox content metadata for {video_id}: {e}')

    scene_name = content_data.get('scene_name') or f'Pornbox_Video_{video_id}'
    medias = content_data.get('medias', [])
    if not medias:
        fail(f'No medias found for video {video_id}')

    # Find media with trailer or full video
    media_item = next((m for m in medias if m.get('type') == 'free' or 'trailer' in str(m.get('title', '')).lower()), None)
    if not media_item:
        media_item = medias[0]

    media_id = media_item.get('media_id')
    if not media_id:
        fail(f'No media_id found for video {video_id}')

    # 2. Fetch stream endpoint
    stream_url = f'https://pornbox.com/media/{media_id}/stream'
    req = urllib.request.Request(stream_url, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=10, context=ctx) as resp:
            stream_data = json.loads(resp.read().decode('utf-8', errors='ignore'))
    except Exception as e:
        fail(f'Failed to fetch stream data for media {media_id}: {e}')

    qualities = stream_data.get('qualities', [])
    if not qualities:
        fail(f'No stream qualities returned for media {media_id}')

    # Pick matching quality
    q_entry = next((q for q in qualities if target_quality in str(q.get('quality', '')).lower() or target_quality in str(q.get('size', '')).lower()), None)
    if not q_entry:
        # Fallback to highest available
        q_entry = qualities[-1]

    direct_url = q_entry.get('src')
    actual_quality = q_entry.get('size') or q_entry.get('quality') or target_quality
    if not actual_quality.endswith('p') and actual_quality.isdigit():
        actual_quality = f'{actual_quality}p'

    page_url = f'https://pornbox.com/application/watch-page/{video_id}'
    print(f'[+] Minted Pornbox stream: video_id={video_id} media_id={media_id} quality={actual_quality}')
    print(f'[+] Direct URL: {direct_url[:80]}...')
    return direct_url, actual_quality, scene_name, page_url

def post_download(video_url, quality, title, page_url):
    # Construct companion extension shaped payload
    payload = {
        'url': video_url,
        'title': f'{title} - {quality}',
        'quality': quality,
        'format': 'mp4',
        'pageUrl': page_url,
        'referrer': page_url,
        'userAgent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36',
        'audioUrl': '',
        'audioReferrer': '',
        'prompt': False,
    }
    req = urllib.request.Request(
        BRIDGE,
        data=json.dumps(payload).encode('utf-8'),
        headers={'Content-Type': 'application/json'}
    )
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            res = json.loads(r.read().decode('utf-8'))
            return res.get('id')
    except Exception as e:
        fail(f'Bridge POST failed: {e}')

def wait_terminal(track_id, start_pos=0):
    deadline = time.time() + TIMEOUT_SEC
    pos = start_pos
    started = False
    start_at = time.time()

    print(f'[+] Polling {LOG} for track {track_id[:8]}...')
    while time.time() < deadline:
        if os.path.exists(LOG):
            try:
                with open(LOG, encoding='utf-8', errors='replace') as f:
                    f.seek(pos)
                    chunk = f.read()
                    pos = f.tell()
                    for line in chunk.splitlines():
                        if track_id[:8] in line:
                            short = line[:200]
                            if 'START' in line or 'BRIDGE-RECV' in line:
                                started = True
                                print(f'  [LOG START] {short}')
                            if 'END OK' in line or 'completed' in line:
                                print(f'  [LOG END OK] {short}')
                                return True
                            if 'END FAILED' in line or 'terminal failure' in line:
                                print(f'  [LOG END FAILED] {short}')
                                fail(f'Download failed in app: {short}')
            except Exception:
                pass

        if not started and (time.time() - start_at > 30):
            fail('No START line within 30s. A modal dialog might be blocking (ensure Settings -> Show download dialog is OFF).')

        time.sleep(POLL_INTERVAL)

    fail(f'Timed out waiting for download to complete after {TIMEOUT_SEC}s')

def verify_output(track_id, expected_quality):
    entry = None
    for _ in range(15):
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
        fail(f'No valid history entry or file on disk found for track_id={track_id}')

    saved_path = entry['SavePath']
    file_size = os.path.getsize(saved_path)
    file_size_mb = file_size / (1024 * 1024)
    print(f'[+] Saved file: {saved_path} ({file_size_mb:.2f} MB)')

    if file_size < 1024 * 1024:
        fail(f'File size is suspiciously small: {file_size} bytes')

    # ffprobe probe streams
    try:
        cmd = [
            'ffprobe', '-v', 'error',
            '-show_entries', 'stream=index,codec_type,codec_name,width,height',
            '-of', 'default=noprint_wrappers=1',
            saved_path
        ]
        out = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
        ff_out = out.stdout.strip()
        print('[+] ffprobe output:')
        for line in ff_out.splitlines():
            print(f'    {line}')
    except Exception as e:
        fail(f'ffprobe execution failed: {e}')

    mh = re.search(r'height=(\d+)', ff_out)
    mw = re.search(r'width=(\d+)', ff_out)
    if not mh or not mw:
        fail('ffprobe did not detect video stream dimensions')

    width = int(mw.group(1))
    height = int(mh.group(1))
    print(f'[+] Video dimensions: {width}x{height}')

    want_h = int(re.search(r'\d+', expected_quality).group(0)) if re.search(r'\d+', expected_quality) else 1080
    if height != want_h:
        fail(f'Resolution mismatch: expected {want_h}p, got {height}p')

    # Check audio stream
    has_audio = 'codec_type=audio' in ff_out
    print(f'[+] Audio stream present: {has_audio}')
    if not has_audio:
        print('[!] Warning: no audio stream detected in media')

    print(f'=== SUCCESS: PORNBOX {expected_quality} SILENT DOWNLOAD VERIFIED ===')
    return 0

def main():
    vid = int(sys.argv[1]) if len(sys.argv) > 1 else 216006
    quality = sys.argv[2] if len(sys.argv) > 2 else '1080p'

    if not bridge_up():
        fail('WrenchDownloader bridge is offline on port 45732')

    video_url, actual_q, title, page_url = get_pornbox_stream(vid, quality)
    start_pos = os.path.getsize(LOG) if os.path.exists(LOG) else 0
    track_id = post_download(video_url, actual_q, title, page_url)
    print(f'[+] POSTed download to bridge: track_id={track_id}')

    wait_terminal(track_id, start_pos)
    ret = verify_output(track_id, actual_q)
    sys.exit(ret)

if __name__ == '__main__':
    main()
