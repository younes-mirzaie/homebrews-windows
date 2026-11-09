# App Loader

On-phone installer for homebrew `.appx`/`.xap` packages (stage 1, branch `app-loader`), growing
into an over-the-air homebrew catalogue (stage 2). Design: [catalog/DESIGN.md](catalog/DESIGN.md).

- `catalog/tools/catalog.py`: build and sign the catalogue index (`keygen`, `add`, `build`, `verify`).
- `catalog/test/test_catalog.py`: checks for it (`python3 catalog/test/test_catalog.py`, needs `cryptography`).
