#!/usr/bin/env bash
# Builds the M3Undle documentation site with MkDocs.
# At this stage (Milestone 1) this only builds MkDocs — no OpenAPI generation yet.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

venv_dir=".venv-docs"
if [ ! -d "$venv_dir" ]; then
  python3 -m venv "$venv_dir"
fi

"$venv_dir/bin/pip" install --quiet --upgrade pip
"$venv_dir/bin/pip" install --quiet -r docs/requirements.txt

"$venv_dir/bin/mkdocs" build --strict "$@"
