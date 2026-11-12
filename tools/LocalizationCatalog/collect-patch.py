"""Collect a guarded localization patch relative to an archived source snapshot."""

import hashlib
import json
from pathlib import Path
import tarfile
import sys


root = Path.cwd().resolve()
manifest = []
archive_name = sys.argv[1] if len(sys.argv) > 1 else "source.tar"
with tarfile.open(root / archive_name) as source, tarfile.open(root / "localized-source-patch.tar", "w") as output:
    for member in source.getmembers():
        name = member.name.replace("\\", "/")
        if not member.isfile() or not name.startswith("src/App/"):
            continue
        if Path(name).suffix not in {".cs", ".xaml", ".json", ".resw"}:
            continue
        path = (root / name).resolve()
        if not path.is_relative_to(root / "src" / "App") or not path.is_file():
            continue
        original = source.extractfile(member).read()
        updated = path.read_bytes()
        if original == updated:
            continue
        output.add(path, arcname=name)
        manifest.append({
            "File": name,
            "OriginalSha256": hashlib.sha256(original).hexdigest(),
            "UpdatedSha256": hashlib.sha256(updated).hexdigest(),
        })
(root / "localized-source-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
print(f"Patch files: {len(manifest)}")
