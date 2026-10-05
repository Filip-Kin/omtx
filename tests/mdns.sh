#!/bin/bash
mkdir -p /run/dbus; dbus-daemon --system --fork; sleep 0.5
sed -i 's/^#\?enable-dbus=.*/enable-dbus=yes/' /etc/avahi/avahi-daemon.conf
avahi-daemon -D --no-chroot 2>/dev/null || avahi-daemon -D; sleep 1
export PATH=/dist:$PATH LD_LIBRARY_PATH=/dist
