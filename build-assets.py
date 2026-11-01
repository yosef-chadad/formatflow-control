"""Restore reproducible binary resources before building the public C# sources."""
from pathlib import Path
import base64, gzip, hashlib, io, json, runpy, urllib.request, zipfile

ROOT = Path(__file__).resolve().parent
RESOURCES = ROOT / 'FormatFlowNative' / 'Resources'
ENGINE_HASH = '2ce797a0f88d7f067180338fb227f7b1928ea727bd9a4d7a1d022f7c52af71a3'

def restore():
    RESOURCES.mkdir(parents=True, exist_ok=True)
    (RESOURCES / 'FormatFlow.ico').write_bytes(base64.b64decode((RESOURCES / 'FormatFlow.ico.b64').read_text()))
    runpy.run_path(str(ROOT / 'FormatFlowNative' / 'Tools' / 'CreateCompletionSound.py'), run_name='__main__')
    engine = RESOURCES / 'ffmpeg.exe.gz'
    if engine.exists() and hashlib.sha256(gzip.decompress(engine.read_bytes())).hexdigest() == ENGINE_HASH:
        return
    with urllib.request.urlopen('https://pypi.org/pypi/imageio-ffmpeg/0.7.0/json', timeout=30) as response:
        metadata = json.load(response)
    wheel = next(f for f in metadata['urls'] if f['filename'].endswith('win_amd64.whl'))
    if not wheel['url'].startswith('https://files.pythonhosted.org/'):
        raise ValueError('Unexpected engine distributor')
    with urllib.request.urlopen(wheel['url'], timeout=180) as response:
        package = response.read(100 * 1024 * 1024 + 1)
    if len(package) > 100 * 1024 * 1024 or hashlib.sha256(package).hexdigest() != wheel['digests']['sha256']:
        raise ValueError('Wheel integrity check failed')
    with zipfile.ZipFile(io.BytesIO(package)) as archive:
        name = next(n for n in archive.namelist() if n.startswith('imageio_ffmpeg/binaries/') and n.endswith('.exe'))
        raw = archive.read(name)
    if hashlib.sha256(raw).hexdigest() != ENGINE_HASH:
        raise ValueError('The FFmpeg executable does not match the pinned engine')
    with engine.open('wb') as output:
        with gzip.GzipFile(fileobj=output, mode='wb', filename='', mtime=0) as compressed:
            compressed.write(raw)

if __name__ == '__main__':
    restore()
