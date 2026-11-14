"""Calls RetrobirdCheck (the background task's entry point) in librbmail.so against the test
servers and the account the in-process test left behind. Usage: check_test.py LIB DATA TOKEN"""
import ctypes
import sys

lib = ctypes.CDLL(sys.argv[1])
args = [b"rbmail", b"--token", sys.argv[3].encode(), b"--data", sys.argv[2].encode()]
argv = (ctypes.c_char_p * len(args))(*args)
out = ctypes.create_string_buffer(65536)
failed = 0
for run in (1, 2):   # twice: the task's process may be reused
    rc = lib.RetrobirdCheck(len(args), argv, out, len(out))
    text = out.value.decode("utf-8", "replace")
    lines = text.splitlines()
    accts = [l.split("\t") for l in lines if l.startswith("A\t")]
    ok = rc == 0 and len(accts) >= 1 and all(len(a) == 6 for a in accts) and all(a[5] == "" for a in accts)
    print("check run %d: %s" % (run, "ok" if ok else "FAILED"), repr(text[:300]))
    failed += not ok
sys.exit(1 if failed else 0)
