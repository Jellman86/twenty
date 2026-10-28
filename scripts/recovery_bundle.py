"""Seal/verify an offline private store copy before opening it on another host.

Does not capture a live store, authenticate its source, repair data, or start an app.
The caller must first establish quiescence and copy into private storage.
"""
import argparse
import hashlib
import json
import os
import re
import sqlite3
import stat
import sys
import unicodedata
from datetime import UTC, datetime
from pathlib import Path, PurePosixPath
from urllib.parse import quote

REPO = Path(__file__).resolve().parents[1]
MAX_FILES = 10000
MAX_BYTES = 512 * 1024 * 1024
VERSION = 'twenty-offline-store-v1'


class BundleError(Exception):
    pass


def private_path(path):
    if path.is_symlink():
        raise BundleError('bundle-link')
    info = path.stat()
    if stat.S_ISREG(info.st_mode) and info.st_nlink != 1:
        raise BundleError('bundle-link')
    if info.st_mode & 0o077 or info.st_uid != os.getuid():
        raise BundleError('bundle-permissions')
    return info


def file_hash(path):
    before = private_path(path)
    if not stat.S_ISREG(before.st_mode) or before.st_nlink != 1:
        raise BundleError('bundle-link')
    if before.st_size > MAX_BYTES:
        raise BundleError('bundle-size')
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(descriptor, 'rb') as stream:
        opened = os.fstat(stream.fileno())
        digest = hashlib.sha256()
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
        after = os.fstat(stream.fileno())
    fields = ('st_dev', 'st_ino', 'st_size', 'st_mtime_ns', 'st_ctime_ns', 'st_nlink')
    if any(getattr(before, field) != getattr(opened, field) or
           getattr(opened, field) != getattr(after, field) for field in fields):
        raise BundleError('bundle-changed-during-read')
    return digest.hexdigest(), after.st_size


def inventory(snapshot):
    if snapshot.resolve().is_relative_to(REPO):
        raise BundleError('bundle-inside-repo')
    private_path(snapshot)
    snapshot = snapshot.resolve()
    rows = []; portable = set(); size = 0
    for path in sorted(snapshot.rglob('*')):
        info = private_path(path)
        name = path.relative_to(snapshot).as_posix()
        key = unicodedata.normalize('NFC', name).casefold()
        if key in portable or not name.isascii() or '\\' in name or any(unicodedata.category(c) == 'Cc' for c in name):
            raise BundleError('bundle-portability')
        portable.add(key)
        if len(portable) > MAX_FILES:
            raise BundleError('bundle-size')
        if stat.S_ISDIR(info.st_mode):
            continue
        if path.name.endswith(('-wal', '-shm', '-journal')):
            raise BundleError('sqlite-sidecar: supply a quiescent offline copy')
        digest, length = file_hash(path); size += length
        rows.append({'path': name, 'bytes': length, 'sha256': digest})
        if len(rows) > MAX_FILES or size > MAX_BYTES:
            raise BundleError('bundle-size')
    if not any(row['path'] == 'research.db' for row in rows):
        raise BundleError('bundle-research-database-missing')
    return rows


def check_databases(snapshot, rows):
    names = [row['path'] for row in rows if row['path'].endswith(('.db', '.sqlite', '.sqlite3'))]
    try:
        for name in names:
            # immutable+read-only prevents SQLite creating WAL/SHM files in the sealed copy.
            uri = 'file:' + quote(str((snapshot / name).resolve()), safe='/') + '?mode=ro&immutable=1'
            with sqlite3.connect(uri, uri=True) as db:
                db.execute('PRAGMA trusted_schema=OFF')
                db.execute('PRAGMA query_only=ON')
                if db.execute('PRAGMA quick_check').fetchall() != [('ok',)]:
                    raise BundleError('sqlite-integrity')
    except sqlite3.Error:
        raise BundleError('sqlite-integrity') from None
    return names


def manifest_location(snapshot, manifest):
    destination = manifest.resolve()
    if destination.is_relative_to(snapshot.resolve()) or destination.is_relative_to(REPO):
        raise BundleError('manifest-location')


def write_new(path, value):
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    private_path(path.parent)
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(descriptor, 'w') as stream:
        stream.write(json.dumps(value, indent=2) + '\n')
        stream.flush(); os.fsync(stream.fileno())


def seal(snapshot, manifest, source_commit):
    manifest_location(snapshot, manifest)
    if manifest.exists() or manifest.is_symlink():
        raise BundleError('manifest-exists')
    if not re.fullmatch('[0-9a-f]{40}', source_commit):
        raise BundleError('source-commit')
    rows = inventory(snapshot); check_databases(snapshot, rows)
    if inventory(snapshot) != rows:
        raise BundleError('bundle-changed-during-check')
    value = {'version': VERSION, 'sourceCommit': source_commit,
             'createdAt': datetime.now(UTC).isoformat(), 'files': rows}
    write_new(manifest, value)
    return file_hash(manifest)[0]


def read_manifest(manifest, expected):
    if not re.fullmatch('[0-9a-f]{64}', expected) or file_hash(manifest)[0] != expected:
        raise BundleError('manifest-pin')
    if manifest.stat().st_size > 8 * 1024 * 1024:
        raise BundleError('manifest-size')
    try:
        value = json.loads(manifest.read_bytes())
        if (set(value) != {'version', 'sourceCommit', 'createdAt', 'files'} or value['version'] != VERSION or
            not re.fullmatch('[0-9a-f]{40}', value['sourceCommit']) or not isinstance(value['createdAt'], str) or
            not isinstance(value['files'], list) or not 0 < len(value['files']) <= MAX_FILES):
            raise ValueError()
        seen = set(); portable = set()
        for row in value['files']:
            if set(row) != {'path', 'bytes', 'sha256'} or not isinstance(row['path'], str):
                raise ValueError()
            path = PurePosixPath(row['path']); key = unicodedata.normalize('NFC', row['path']).casefold()
            if (path.is_absolute() or path.as_posix() != row['path'] or '..' in path.parts or not path.parts or
                row['path'] in seen or key in portable or not row['path'].isascii() or '\\' in row['path'] or
                type(row['bytes']) is not int or not 0 <= row['bytes'] <= MAX_BYTES or
                not re.fullmatch('[0-9a-f]{64}', row['sha256'])):
                raise ValueError()
            seen.add(row['path']); portable.add(key)
        if value['files'] != sorted(value['files'], key=lambda row: row['path']):
            raise ValueError()
    except (ValueError, KeyError, TypeError):
        raise BundleError('manifest-contract') from None
    # Refuse changes between the pin check and parsing as well.
    if file_hash(manifest)[0] != expected:
        raise BundleError('manifest-pin')
    return value


def verify(snapshot, manifest, expected):
    manifest_location(snapshot, manifest)
    value = read_manifest(manifest, expected)
    rows = inventory(snapshot)
    if rows != value['files']:
        raise BundleError('bundle-content')
    databases = check_databases(snapshot, rows)
    if inventory(snapshot) != rows:
        raise BundleError('bundle-changed-during-check')
    return {'status': 'passed', 'manifestSha256': expected, 'sourceCommit': value['sourceCommit'],
            'files': len(rows), 'bytes': sum(row['bytes'] for row in rows), 'sqliteChecked': databases,
            'scope': 'offline-byte-integrity-and-sqlite-only'}


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    create = commands.add_parser('seal'); create.add_argument('--source-commit', required=True)
    check = commands.add_parser('verify'); check.add_argument('--expected-sha256', required=True)
    for command in (create, check):
        command.add_argument('--snapshot', required=True, type=Path)
        command.add_argument('--manifest', required=True, type=Path)
    args = parser.parse_args()
    try:
        if args.command == 'seal':
            pin = seal(args.snapshot, args.manifest, args.source_commit)
            result = {'status': 'sealed', 'manifestSha256': pin}
        else:
            result = verify(args.snapshot, args.manifest, args.expected_sha256)
        print(json.dumps(result)); return 0
    except (BundleError, OSError) as error:
        # File contents, account identifiers and SQLite exception payloads stay out of output.
        code = str(error).split(':', 1)[0] if isinstance(error, BundleError) else 'bundle-io'
        print(json.dumps({'status': 'failed', 'error': code})); return 1


if __name__ == '__main__':
    sys.exit(main())
