# Linux build of rbmaild, for testing the mail stack without a phone (system NSS/NSPR).
# The phone build is ..\build.ps1 (clang-cl, ARM, retrofox's static NSS).
CC ?= cc
CFLAGS ?= -O1 -g -Wall -Wextra -Wno-unused-parameter -Wno-sign-compare -Wno-missing-field-initializers
NSS_CFLAGS := $(shell pkg-config --cflags nss nspr)
NSS_LIBS := $(shell pkg-config --libs nss nspr)
SRCS := server.c imap.c smtp.c mime.c codec.c net.c store.c buf.c
OBJS := $(SRCS:%.c=build/%.o)

all: build/rbmaild build/mimetest

build/%.o: %.c rb.h
	@mkdir -p build
	$(CC) $(CFLAGS) $(NSS_CFLAGS) -c $< -o $@

build/rbmaild: $(OBJS)
	$(CC) $(CFLAGS) -o $@ $(OBJS) $(NSS_LIBS) -lpthread

# MIME/charset/HTML unit checks (no network)
build/mimetest: test/mimetest.c build/mime.o build/codec.o build/buf.o build/net.o build/store.o build/imap.o
	$(CC) $(CFLAGS) $(NSS_CFLAGS) -I. -o $@ $^ $(NSS_LIBS) -lpthread

asan:
	$(MAKE) clean
	$(MAKE) CFLAGS="-O1 -g -fsanitize=address,undefined -fno-omit-frame-pointer -Wall -Wextra -Wno-unused-parameter -Wno-sign-compare -Wno-missing-field-initializers"

clean:
	rm -rf build

.PHONY: all clean asan
