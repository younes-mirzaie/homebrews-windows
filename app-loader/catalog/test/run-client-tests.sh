#!/bin/sh
# Signs a test index with tools/catalog.py, then runs the C# client checks on Mono.
set -e
here=$(cd "$(dirname "$0")" && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
CATALOG_VECTORS="$tmp/vec" python3 "$here/test_catalog.py" > /dev/null
mcs -nologo -out:"$tmp/t.exe" "$here/CatalogTest.cs" "$here"/../client/*.cs
mono "$tmp/t.exe" "$tmp/vec"
