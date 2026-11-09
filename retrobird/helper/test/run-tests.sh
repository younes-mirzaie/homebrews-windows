#!/bin/sh
# Builds rbmaild for Linux, starts a throwaway Dovecot (IMAP) and a fake SMTP server with a
# self-signed certificate, then runs the protocol tests against them. Needs: dovecot-imapd,
# libnss3-dev, openssl, python3. Run as root (Dovecot drops to the 'vmail' user, made if missing).
set -e
here=$(cd "$(dirname "$0")" && pwd)
make -C "$here/.." >/dev/null
"$here/../build/mimetest"

dir=$(mktemp -d /tmp/rbtest.XXXXXX)
user=vmail
id "$user" >/dev/null 2>&1 || useradd -r -M -s /usr/sbin/nologin "$user"
cleanup() {
    [ -f "$dir/smtp.pid" ] && kill "$(cat "$dir/smtp.pid")" 2>/dev/null || true
    timeout 10 doveadm -c "$dir/dovecot.conf" stop 2>/dev/null || pkill -f "dovecot -c $dir/dovecot.conf" || true
    [ -n "$KEEP" ] || rm -rf "$dir"
}
trap cleanup EXIT

openssl req -x509 -newkey rsa:2048 -nodes -days 2 -subj "/CN=localhost" \
    -keyout "$dir/server.key" -out "$dir/server.crt" 2>/dev/null
echo "test@localhost:{PLAIN}s3cret p\xc3\xa4ss" | sed 's/\\xc3\\xa4/ä/' > "$dir/users"
mkdir -p "$dir/run" "$dir/state" "$dir/mail/test@localhost" "$dir/outbox"
for d in cur new tmp; do mkdir -p "$dir/mail/test@localhost/$d"; done
sed -e "s|@DIR@|$dir|g" -e "s|@USER@|$user|g" "$here/dovecot.conf.in" > "$dir/dovecot.conf"
chown -R "$user:$user" "$dir/mail"
chmod 755 "$dir"
dovecot -c "$dir/dovecot.conf"
python3 -I "$here/fake_smtp.py" "$dir/server.crt" "$dir/server.key" "$dir/outbox" \
    "test@localhost" "s3cret päss" 10465 10587 &
echo $! > "$dir/smtp.pid"
sleep 1
RB_TEST_DIR="$dir" RB_MAIL_USER="$user" python3 -u -I "$here/test_rbmaild.py" "$here/../build/rbmaild"

# The app's C# protocol client, on Mono, against a fresh helper
if command -v mcs >/dev/null && command -v mono >/dev/null; then
    core="$here/../../app/Retrobird/Core"
    mcs -nologo -out:"$dir/ClientTest.exe" "$here/../../app/tests/ClientTest.cs" \
        "$core/LineSocket.cs" "$core/MailClient.cs" "$core/Models.cs" "$core/Providers.cs"
    "$here/../build/rbmaild" --token mono-token-12345 --port 18097 --data "$dir/monodata" --sync-minutes 0 &
    helper=$!
    sleep 0.5
    status=0
    mono "$dir/ClientTest.exe" 18097 mono-token-12345 "s3cret päss" || status=$?
    kill $helper 2>/dev/null || true
    [ $status -eq 0 ] || exit $status
    # the same checks with the helper loaded as a library inside the Mono process (as in the app)
    mcs -nologo -main:InProcessTest -out:"$dir/InProcessTest.exe" "$here/../../app/tests/InProcessTest.cs" \
        "$here/../../app/tests/ClientTest.cs" "$core/LineSocket.cs" "$core/MailClient.cs" "$core/Models.cs" "$core/Providers.cs"
    echo "in-process:"
    LD_LIBRARY_PATH="$here/../build" timeout -k 5 180 mono "$dir/InProcessTest.exe" 18098 mono-token-12345 "s3cret päss" "$dir/inprocdata"
    exit $?
else
    echo "(mcs/mono not installed: skipping the C# client test)"
fi
