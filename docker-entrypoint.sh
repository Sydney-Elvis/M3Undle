#!/bin/sh
set -e

# When bind mounts are used on Linux, the host-created directories are owned
# by root and not writable by the 'app' user. Fix ownership before starting.
chown -R app:app /data /config

exec gosu app "$@"
