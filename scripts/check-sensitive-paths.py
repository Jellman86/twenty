"""Reject credential and private replay paths even if contents evade pattern scanning."""
import subprocess
from pathlib import PurePosixPath

paths = subprocess.check_output(["git", "ls-files", "-z"], text=True).split("\0")
blocked = []
for name in filter(None, paths):
    path = PurePosixPath(name.lower())
    if (".agents" in path.parts or "nevercommit" in path.parts or "raw-replays" in path.parts or
            path.name == "generalauth.md" or path.name == ".env" or
            path.name.startswith(".env.") or path.suffix in {".pem", ".key", ".p12", ".pfx"}):
        blocked.append(name)
if blocked:
    raise SystemExit("Sensitive paths must not be tracked: " + ", ".join(blocked))
print("Sensitive path guard passed")
