# Linux build of rfxfetch for tests (system NSS). The phone build is ..\build-fetch.ps1.
CC ?= cc
CFLAGS ?= -O1 -g -Wall -Wextra -Wno-unused-parameter -Wno-sign-compare
NSS_CFLAGS := $(shell pkg-config --cflags nss nspr)
NSS_LIBS := $(shell pkg-config --libs nss nspr)

build/rfxfetch: rfxfetch.c
	@mkdir -p build
	$(CC) $(CFLAGS) $(NSS_CFLAGS) -o $@ $< $(NSS_LIBS)

asan:
	rm -rf build
	$(MAKE) CFLAGS="-O1 -g -fsanitize=address,undefined -fno-omit-frame-pointer -Wall -Wextra -Wno-unused-parameter -Wno-sign-compare"

clean:
	rm -rf build
.PHONY: asan clean
