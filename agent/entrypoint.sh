#!/bin/sh
# Fix ownership of /library so non-root agent can write to it
if [ -d /library ]; then
    # Run as root just to fix perms, then drop privileges
    if [ "$(id -u)" = "0" ]; then
        chown -R piagent:piagent /library
        echo "Fixed /library ownership for piagent"
        exec su-exec piagent "$@"
    fi
fi

exec "$@"
