# App Loader stage 2: OTA homebrew catalogue

Stage 1 (branch `app-loader`, built by the browser thread) installs `.appx`/`.xap` files already
on the phone: it lists packages in Downloads, shows installed apps, and runs `TH.exe` as SYSTEM
through the phone's own loopback shell (update first, fresh install second, never uninstall).
Stage 2 adds a catalogue so the loader can browse, install and update homebrew from anywhere
(Wi-Fi or cellular) with nothing but the phone and a public GitHub repo.

## Goals and rules

- Phone only. No PC, no own server. A static GitHub repo/Pages site is the only thing online.
- Works on cellular, not just home Wi-Fi.
- A hostile network, a hacked mirror, or a swapped GitHub release must not get code installed.
  Everything installs as SYSTEM-deployed sideloads, so integrity is the main risk.
- Never uninstall to "fix" a failed update (the CMD Injector SD card incident).

## Pieces

```
GitHub repo (public)                     Phone
  catalog/apps/<id>.json   --build-->      App Loader (Silverlight 8.1)
  dist/index.json (+ .sig) ------------->    1. fetch index + sig  (rfxfetch)
  Release assets *.appx/*.xap ---------->    2. verify Ed25519 sig, serial >= last seen
                                             3. show apps, compare with installed versions
                                             4. fetch package       (rfxfetch, sha256 checked)
                                             5. install via stage 1 installer (TH.exe)
```

### 1. Catalogue format (`tools/catalog.py`, tested by `test/test_catalog.py`)

One file per app in `apps/<id>.json`, written by `catalog.py add <pkg> --url <release url>`, which
reads the package manifest (appx, appxbundle, 8.1 Silverlight xap, 8.0 xap) and records:

| field | meaning |
|---|---|
| `id`, `name`, `author`, `description`, `license`, `source`, `icon` | listing info (filled by hand after `add`) |
| `releases[]` | newest first: `version` (a.b.c.d), `kind`, `arch`, `package_name`, `publisher`, `url` (https only), `sha256`, `size`, `min_os`, `notes`, `added` |

`catalog.py build --key KEY.pem` merges them into `dist/index.json` (canonical JSON) with a
`serial` that never goes backwards, and writes `index.json.sig` (base64 Ed25519 over the exact
bytes). Older releases stay listed so a user can roll back on purpose.

### 2. Trust

- **Index signature**: Ed25519 public key compiled into the loader. The phone refuses an index
  whose signature fails, or whose `serial` is lower than the highest it has accepted (blocks
  replaying an old index with a known-bad version).
- **Packages**: pinned by `sha256` + `size` in the signed index. The URL can point anywhere
  (GitHub Releases, a community mirror, archive.org); a mismatch is deleted, never installed.
- **Key handling**: the private key stays offline on the publisher's PC (`*.pem` is git-ignored). Signing
  happens on the PC when publishing, not in CI, so a leaked GitHub token can't publish.
  Losing the key means shipping a loader update with a new public key, so keep a backup.
- Transport security is then a bonus, not the guarantee.

### 3. Downloads: `rfxfetch` (native, NSS)

WP 8.1's own TLS stack can't talk to GitHub's current servers (same reason rfxproxy exists).
Options:

| option | Wi-Fi | cellular | notes |
|---|---|---|---|
| **rfxfetch.exe** (recommended) | yes | yes | small NSS client from the retrofox step 2 `tlstest`; run as SYSTEM via the same shell stage 1 uses; follows redirects (GitHub release URLs redirect to `objects.githubusercontent.com`), writes to a temp file, checks sha256/size, renames |
| rfxproxy on :8080 | yes | no | system proxy is per Wi-Fi network, and a Silverlight app can't set a proxy |
| WP 8.1 HttpClient direct | no | no | TLS handshake fails against GitHub |

`rfxfetch <url> <out> [--sha256 H --size N] [--max-bytes N]` exits 0 only on a verified file,
prints progress lines (`PROGRESS <done> <total>`) that the app polls from a log, and refuses
anything over `--max-bytes` (index: 1 MB). Built with the retrofox toolchain like rfxfsd/rbmaild.
Strict cert validation with NSS's root store (Retrobird's `rbmaild` already does this).

### 4. Loader UI (Metro, per `skills/metro-ui-design`)

Pivot with three panes, added beside stage 1's:

- **catalogue**: list of apps (icon, name, one-line description, "installed 1.0 / 1.1 available").
  Tap opens a details page: description, author, license, source link, release notes, size,
  install/update button, older versions under "more".
- **updates**: installed apps with a newer catalogue release; "update all" app bar button.
- **local** (stage 1): packages already on the phone.

Settings: catalogue URL (default the official one; extra community catalogues each pinned to
their own public key), "check for updates" on/off. A Silverlight periodic background agent can
check once a day and set a live tile count; it only fetches the index, never installs.

### 5. Install flow

1. Download to `C:\Data\SharedData\AppLoader\cache\<sha256>.<ext>` (ASCII path, SYSTEM-readable).
2. Re-check sha256 in the app before handing over (cheap, catches cache corruption).
3. Check the catalogue's `package_name` matches what is installed (if anything): an update can't
   silently replace a different app.
4. Stage 1 installer: update (`-ua`/`-uda`), on refusal show the TH.exe error. Known case:
   `0x8103012e` = installed on SD and the package opts out of SD: tell the user to move it to the
   phone in Storage Sense. No automatic uninstall.
5. Delete the cached package after a successful install (keep last one on failure for retry).

## Publishing (how an app gets in)

```
python catalog/tools/catalog.py add Retrofox_0.3.1.0_ARM.appx --url https://github.com/.../releases/download/retrofox-0.3.1/Retrofox_0.3.1.0_ARM.appx
# edit apps/<id>.json: description, license, icon
python catalog/tools/catalog.py build --key C:\keys\wp-homebrew.pem
git add apps dist && git commit && git push      # GitHub Pages serves dist/
```

Only list apps we may redistribute (own apps, open-source homebrew with a license that allows
it). Old Store apps from archives: link to their existing archive URL with a pinned hash rather
than rehosting.

## Open questions

1. ~~Repo for the catalogue~~ Decided 2026-11-09: new public repo `wp-homebrew` (GitHub Pages
   serves `dist/`). The tool stays here; the repo holds `apps/` and `dist/`.
2. Community catalogues: allow adding other people's catalogues (each with its own key), or keep
   it to the official one for now?

## Stage 2 build order

1. `rfxfetch` native helper + Wine/Linux tests (NSS, redirects, hash check, size cap).
2. C# Ed25519 verify (small TweetNaCl port) + index parser + serial store, unit-tested on Mono.
3. Catalogue/updates panes on top of stage 1's installer class.
4. Background update check + live tile.
