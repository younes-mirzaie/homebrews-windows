# rfxfetch

Native HTTPS downloader for the App Loader's catalogue (stage 2). WP 8.1's own TLS can't reach
GitHub any more; this uses NSS (TLS 1.2/1.3, Mozilla roots, strict name checks) like Retrobird,
and works on Wi-Fi and mobile data (no proxy needed).

```
rfxfetch <https-url> <out-file> [--sha256 HEX] [--size N] [--max-bytes N] [--roots MODULE] [--ca PEM] [--timeout S] [--quiet]
```

stdout: `PROGRESS <bytes> <total|-1>` lines, then `OK <sha256> <bytes>` or `ERROR <text>`.
Exit 0 ok, 1 network/HTTP/TLS, 2 hash or size mismatch, 3 usage. The file appears only when
verified (written as `<out>.part`, renamed at the end); an existing `<out>` is kept on failure.
https only, also for redirects (max 5). Default size cap 64 MB.

- Phone build: `.\build.ps1` on the build PC (retrofox toolchain), output goes to `..\app\AppLoader\Helper`.
- Linux tests: `make && python3 test/test_rfxfetch.py` (needs libnss3-dev, python `cryptography`): 16 checks.
