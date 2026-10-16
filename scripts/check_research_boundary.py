"""Build-probe the production-style assembly boundary without opening any dataset or holdout."""
import hashlib
import shutil
import subprocess
import tempfile
from pathlib import Path

source = Path(__file__).resolve().parents[1]


def run(*args, cwd):
    result = subprocess.run(args, check=False, cwd=cwd, capture_output=True, text=True, timeout=120)
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)


def hashes(root):
    directory = root / "src/Twenty.Api/bin/Release/net10.0"
    return {name: hashlib.sha256((directory / f"Twenty.{name}.dll").read_bytes()).hexdigest()
            for name in ("Core", "Research", "Api")}


def replace(path, before, after):
    content = path.read_text()
    assert content.count(before) == 1, f"Probe trigger changed: {path.name}"
    path.write_text(content.replace(before, after))


with tempfile.TemporaryDirectory(prefix="twenty-boundary-") as temporary:
    root = Path(temporary)
    for name in ("global.json", "Directory.Build.props"):
        shutil.copyfile(source / name, root / name)
    for project in ("Twenty.Core", "Twenty.Research", "Twenty.Api"):
        shutil.copytree(source / "src" / project, root / "src" / project,
                        ignore=shutil.ignore_patterns("bin", "obj", "wwwroot"))
    # No .git is copied, matching Docker's build context; commit metadata is recorded by the host separately.
    api = "src/Twenty.Api/Twenty.Api.csproj"
    run("dotnet", "restore", api, "--locked-mode", cwd=root)
    run("dotnet", "build", api, "-c", "Release", "--no-restore", cwd=root)
    original = hashes(root)
    run("dotnet", "build", api, "-c", "Release", "--no-restore", "-p:SourceRevisionId=" + "1" * 40, cwd=root)
    revision_a = hashes(root)
    run("dotnet", "build", api, "-c", "Release", "--no-restore", "-p:SourceRevisionId=" + "2" * 40, cwd=root)
    revision_b = hashes(root)
    for name in ("Core", "Research"):
        assert original[name] == revision_a[name] == revision_b[name], "Git revision metadata invalidated scientific artifacts"
    replace(root / "src/Twenty.Api/Program.cs", 'name = "Twenty"', 'name = "API-only boundary probe"')
    run("dotnet", "build", api, "-c", "Release", "--no-restore", cwd=root)
    adapter = hashes(root)
    assert adapter["Api"] != original["Api"]
    assert adapter["Core"] == original["Core"] and adapter["Research"] == original["Research"], "API-only edit invalidated scientific artifacts"
    replace(root / "src/Twenty.Core/Research/StudyProtocol.cs", "RidgeAlpha: 10", "RidgeAlpha: 11")
    run("dotnet", "build", api, "-c", "Release", "--no-restore", cwd=root)
    model = hashes(root)
    assert model["Core"] != adapter["Core"], "Model-policy edit did not change scientific artifacts"
    replace(root / "src/Twenty.Core/Data/DatasetJson.cs", "8 * 1024 * 1024", "8 * 1024 * 1024 - 1")
    run("dotnet", "build", api, "-c", "Release", "--no-restore", cwd=root)
    reader = hashes(root)
    assert reader["Core"] != model["Core"], "Transitive reader limit was omitted from scientific artifacts"
    print("Research boundary passed: API-only edits and Git revisions preserve scientific artifacts; model policy and reader edits change them")
