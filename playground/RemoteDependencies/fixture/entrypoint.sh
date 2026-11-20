#!/bin/sh
set -eu
ssh-keygen -A
# A bind mount retains the developer UID; StrictModes requires root-owned server key material.
cp /run/fixture-authorized-key /root/.ssh/authorized_keys
chown 0:0 /root/.ssh/authorized_keys
chmod 600 /root/.ssh/authorized_keys
# A forcibly stopped fixture can retain pidfiles whose PIDs were reused on restart.
rm -f /var/run/docker.pid /var/run/docker/containerd/containerd.pid
dockerd --host=unix:///var/run/docker.sock --storage-driver=vfs >/var/log/dockerd.log 2>&1 &
engine=$!
trap 'kill "$engine"' EXIT
until docker info >/dev/null 2>&1; do
    if ! kill -0 "$engine" 2>/dev/null; then
        cat /var/log/dockerd.log
        exit 1
    fi
    sleep 1
done
/usr/sbin/sshd -D -e
