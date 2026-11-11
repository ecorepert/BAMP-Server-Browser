#!/bin/sh
# Installs the BAMP server (game list + shared world storage) on a Linux VPS.
# Requirements: Debian/Ubuntu (or any systemd distribution) and python3.
#
# Usage (as root, in the folder that contains master.py and this script):
#   sh install.sh            # port 8787
#   sh install.sh 9000       # other port
#   sh install.sh 8787 latest   # download the latest master.py from GitHub instead
#
# One line from your PC (what the game's "My own server" window copies):
#   ssh -t root@YOUR_VPS "curl -fsSL https://github.com/ecorepert/BAMP-Server-Browser/raw/main/master/install.sh -o /tmp/bamp-install.sh && sudo sh /tmp/bamp-install.sh 8787 latest"
set -e
PORT="${1:-8787}"
SOURCE="${2:-}"
DIR=/opt/bamp-master
MASTER_URL="https://github.com/ecorepert/BAMP-Server-Browser/raw/main/master/master.py"

if [ "$(id -u)" != "0" ]; then echo "Run as root (sudo sh install.sh)"; exit 1; fi
case "$PORT" in ''|*[!0-9]*) echo "Invalid port: $PORT"; exit 1;; esac
command -v python3 >/dev/null 2>&1 || { echo "python3 missing: apt install -y python3"; exit 1; }
if [ "$SOURCE" = "latest" ] || [ ! -f master.py ]; then
  command -v curl >/dev/null 2>&1 || { echo "curl missing: apt install -y curl"; exit 1; }
  WORK=$(mktemp -d)
  echo "Downloading master.py from GitHub..."
  curl -fsSL "$MASTER_URL" -o "$WORK/master.py" || { echo "Download failed: $MASTER_URL"; exit 1; }
  cd "$WORK"
fi
[ -f master.py ] || { echo "master.py not found in the current folder"; exit 1; }
python3 -c "import ast,sys; ast.parse(open('master.py').read())" || { echo "master.py is not valid Python"; exit 1; }
if [ -f "$DIR/master.py" ]; then cp -p "$DIR/master.py" "$DIR/master.py.bak"; fi

mkdir -p "$DIR"
cp master.py "$DIR/master.py"
chmod 644 "$DIR/master.py"

cat > /etc/systemd/system/bamp-master.service <<EOF
[Unit]
Description=BAMP Server Browser (Big Ambitions game list + shared worlds)
After=network-online.target
Wants=network-online.target

[Service]
ExecStart=/usr/bin/env python3 $DIR/master.py
Environment=BAMP_MASTER_PORT=$PORT
DynamicUser=yes
StateDirectory=bamp-master
Restart=always
RestartSec=3
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
PrivateTmp=yes
MemoryMax=256M
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectKernelLogs=yes
ProtectControlGroups=yes
ProtectClock=yes
ProtectHostname=yes
ProtectProc=invisible
ProcSubset=pid
PrivateDevices=yes
PrivateUsers=yes
RestrictAddressFamilies=AF_INET AF_INET6
RestrictNamespaces=yes
RestrictRealtime=yes
RestrictSUIDSGID=yes
LockPersonality=yes
MemoryDenyWriteExecute=yes
SystemCallArchitectures=native
SystemCallFilter=@system-service
SystemCallFilter=~@privileged @resources
CapabilityBoundingSet=
AmbientCapabilities=
UMask=0077
TasksMax=128
LimitNOFILE=1024

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now bamp-master
systemctl restart bamp-master

if command -v ufw >/dev/null 2>&1 && ufw status | grep -q "Status: active"; then
  ufw allow "$PORT/tcp" comment "BAMP master" >/dev/null
  echo "ufw firewall: port $PORT/tcp opened."
else
  echo "Remember to open port $PORT/tcp in your hosting provider firewall if needed."
fi

sleep 1
if command -v curl >/dev/null 2>&1 && curl -fs "http://127.0.0.1:$PORT/health" >/dev/null; then
  IP=$(curl -fs https://api.ipify.org 2>/dev/null || hostname -I | awk '{print $1}')
  echo "OK: server running on port $PORT."
  echo "In the admin config.json:  \"worldsUrl\": \"http://$IP:$PORT\""
else
  echo "Service installed; check with: systemctl status bamp-master"
fi
