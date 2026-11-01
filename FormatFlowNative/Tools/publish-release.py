"""Publish a built EXE from an authorized GitHub Actions run, then update its feed."""
import base64, hashlib, json, os, re, urllib.error, urllib.request
from pathlib import Path
import xml.etree.ElementTree as ET

REPO = 'yosef-chadad/formatflow-control'
API = 'https://api.github.com/repos/' + REPO

def request(url, method='GET', body=None, binary=False):
    if not (url.startswith(API + '/') or url.startswith('https://uploads.github.com/repos/' + REPO + '/')):
        raise ValueError('Unexpected publication endpoint')
    data = body if binary else (json.dumps(body).encode() if body is not None else None)
    headers = {'Authorization': 'Bearer ' + os.environ['GITHUB_TOKEN'], 'Accept': 'application/vnd.github+json',
               'X-GitHub-Api-Version': '2022-11-28', 'Content-Type': 'application/octet-stream' if binary else 'application/json'}
    with urllib.request.urlopen(urllib.request.Request(url, data=data, method=method, headers=headers), timeout=180) as response:
        return json.load(response)

def version_tuple(value):
    if not re.fullmatch(r'\d+\.\d+\.\d+(?:\.\d+)?', value):
        raise ValueError('Invalid release version')
    return tuple(map(int, (value + '.0').split('.')[:4]))

def publish():
    if os.environ.get('GITHUB_REPOSITORY') != REPO or os.environ.get('GITHUB_REF') != 'refs/heads/main':
        raise ValueError('Only the owner repository main branch can publish')
    version = ET.parse('FormatFlowNative/FormatFlow.csproj').findtext('.//Version')
    version_tuple(version)
    exe = Path('FormatFlowNative/bin/Release/net48/FormatFlow.exe').read_bytes()
    if exe[:2] != b'MZ':
        raise ValueError('Expected a Windows executable')
    digest = hashlib.sha256(exe).hexdigest()
    tag = 'v' + version
    try:
        release = request(API + '/releases/tags/' + tag)
    except urllib.error.HTTPError as error:
        if error.code != 404:
            raise
        release = request(API + '/releases', 'POST', {'tag_name': tag, 'target_commitish': os.environ['GITHUB_SHA'],
            'name': 'FormatFlow ' + version, 'draft': True, 'prerelease': False,
            'body': 'Portable Windows EXE. Two advertisement positions: compact above the greeting and lower banner after selected files. Public sources included in this tag.'})
    asset = next((a for a in release.get('assets', []) if a['name'] == 'FormatFlow.exe'), None)
    if asset:
        if asset.get('digest') != 'sha256:' + digest:
            raise ValueError('An existing release asset differs. Increment the application version; do not overwrite it.')
    else:
        asset = request('https://uploads.github.com/repos/' + REPO + '/releases/' + str(release['id']) + '/assets?name=FormatFlow.exe', 'POST', exe, binary=True)
        if asset.get('digest') and asset['digest'] != 'sha256:' + digest:
            raise ValueError('GitHub asset integrity mismatch')
    if release['draft']:
        request(API + '/releases/' + str(release['id']), 'PATCH', {'draft': False})
    # Read immediately before the write and use the blob SHA as a concurrency guard.
    file = request(API + '/contents/feed.json?ref=main')
    feed = json.loads(base64.b64decode(file['content']))
    if feed.get('schema') != 1:
        raise ValueError('Unsupported live feed schema')
    if version_tuple(feed.get('latest', {}).get('version', '0.0.0')) > version_tuple(version):
        raise ValueError('A newer release is already active; refusing to downgrade')
    feed['latest'] = {'version': version, 'url': 'https://github.com/' + REPO + '/releases/download/' + tag + '/FormatFlow.exe',
                      'sha256': digest, 'installMode': 'onNextLaunch'}
    feed['revision'] = int(feed.get('revision', 0)) + 1
    content = base64.b64encode((json.dumps(feed, ensure_ascii=False, indent=2) + '\n').encode()).decode()
    request(API + '/contents/feed.json', 'PUT', {'message': 'Activate verified FormatFlow ' + version,
            'branch': 'main', 'sha': file['sha'], 'content': content})
    print('Published version ' + version + ' with SHA-256 ' + digest)

if __name__ == '__main__':
    publish()
