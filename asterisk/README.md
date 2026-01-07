# Taxi AI Asterisk Bridge - Installation Guide

## Prerequisites

```bash
# Install Python dependencies
pip3 install websockets numpy
```

## Installation

1. **Copy files to server:**
```bash
sudo mkdir -p /opt/taxi-bridge
sudo cp taxi_bridge.py /opt/taxi-bridge/
sudo chown -R asterisk:asterisk /opt/taxi-bridge
sudo chmod +x /opt/taxi-bridge/taxi_bridge.py
```

2. **Install systemd service:**
```bash
sudo cp taxi-bridge.service /etc/systemd/system/
sudo systemctl daemon-reload
```

3. **Enable and start the service:**
```bash
sudo systemctl enable taxi-bridge
sudo systemctl start taxi-bridge
```

## Management Commands

```bash
# Check status
sudo systemctl status taxi-bridge

# View logs
sudo journalctl -u taxi-bridge -f

# Restart service
sudo systemctl restart taxi-bridge

# Stop service
sudo systemctl stop taxi-bridge
```

## Asterisk Dialplan Configuration

Add to your `/etc/asterisk/extensions.conf`:

```ini
[taxi-ai]
exten => 100,1,Answer()
 same => n,Wait(0.5)
 same => n,AudioSocket(127.0.0.1:9092)
 same => n,Hangup()

; Or for a DID number:
exten => _X.,1,Answer()
 same => n,Wait(0.5)
 same => n,AudioSocket(127.0.0.1:9092)
 same => n,Hangup()
```

Then reload Asterisk:
```bash
asterisk -rx "dialplan reload"
```

## Troubleshooting

### Check if bridge is listening
```bash
ss -tlnp | grep 9092
```

### View real-time logs
```bash
sudo journalctl -u taxi-bridge -f
```

### Test AudioSocket connection
```bash
nc -zv 127.0.0.1 9092
```

## Firewall (if needed)

```bash
# Only if bridge runs on different server than Asterisk
sudo ufw allow 9092/tcp
```
