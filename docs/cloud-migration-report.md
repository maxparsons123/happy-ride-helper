# Ada Voice AI — Cloud Migration Feasibility Report

**Prepared for:** Customer Stakeholders  
**Date:** February 2026  
**Version:** 1.0

---

## Executive Summary

Ada is currently deployed as an on-premise .NET 8 application that bridges SIP telephony with OpenAI's Realtime API to provide automated taxi booking by voice. This report assesses the requirements, costs, challenges, and infrastructure changes needed to transition Ada into a fully cloud-hosted, web-managed solution.

**Key finding:** A full cloud migration is achievable but involves significant architectural changes across three domains — telephony infrastructure, real-time audio processing, and operational management. The transition is not a simple "lift and shift" but a re-platforming effort with estimated 8–14 weeks of engineering work and ongoing monthly costs of approximately **£800–£2,500/month** depending on call volume.

---

## 1. Current Architecture (On-Premise)

### 1.1 System Overview

```
┌──────────────┐      SIP/RTP        ┌─────────────────────┐     WebSocket      ┌─────────────┐
│  SIP Trunk   │◄────────────────────►│  Ada .NET Server    │◄──────────────────►│  OpenAI      │
│  (Provider)  │   G.711 A-law 8kHz  │  (Windows/Linux)    │   G.711 passthrough│  Realtime    │
└──────────────┘                      ├─────────────────────┤                    └─────────────┘
                                      │  SIPSorcery (SIP)   │
                                      │  RTP Playout Engine  │       REST/HTTPS
                                      │  Session Manager     │◄──────────────────►  Edge Functions
                                      │  Fare Calculator     │                     (Supabase)
                                      │  BSQD Dispatcher     │
                                      └─────────────────────┘
```

### 1.2 Current Components

| Component | Technology | Location |
|-----------|-----------|----------|
| SIP Server | SIPSorcery (.NET 8) | On-premise server |
| Audio Bridge | Custom G.711 A-law RTP playout with 20ms timing | On-premise |
| AI Engine | OpenAI Realtime API (gpt-4o-mini-realtime) | Cloud (OpenAI) |
| Address Resolution | Photon geocoder + Gemini disambiguation | Cloud (Edge Functions) |
| Fare Calculation | Haversine distance-based (£3.50 base + £1/mile) | Cloud (Edge Function) |
| Dispatch | BSQD webhook integration | Cloud (BSQD) |
| Database | Supabase (callers, bookings, live_calls, agents) | Cloud (Lovable Cloud) |
| Admin Dashboard | Lovable web app | Cloud |
| Desktop Client | WinForms (optional manual operator) | On-premise Windows PC |

### 1.3 What Already Runs in the Cloud

Critically, several components **already operate in the cloud**:
- **Database & state management** — Supabase (Lovable Cloud)
- **Edge Functions** — address resolution, fare calculation, dispatch callbacks
- **OpenAI Realtime API** — the AI brain itself
- **Admin dashboard** — web-based agent configuration

**The only component requiring migration is the SIP/RTP bridge** — the .NET server that handles telephony.

---

## 2. Cloud Architecture Options

### Option A: Cloud VPS with SIP Bridge (Recommended — Lowest Risk)

Deploy the existing .NET headless server (`AdaMain.Server`) on a cloud VPS. This is the most pragmatic approach with minimal code changes.

```
┌──────────────┐     SIP Trunk      ┌────────────────────┐
│  PSTN / SIP  │◄──────────────────►│  Cloud VPS         │
│  Provider    │                    │  (Ada .NET Server)  │
└──────────────┘                    │  Ubuntu 22.04       │
                                    │  Docker / systemd   │
                                    └────────┬───────────┘
                                             │ All existing cloud
                                             │ integrations remain
                                             ▼
                                    ┌────────────────────┐
                                    │  Existing Cloud     │
                                    │  (OpenAI, Supabase, │
                                    │   Edge Functions)   │
                                    └────────────────────┘
```

### Option B: Fully Serverless (WebRTC + Twilio/Vonage)

Replace SIP/RTP entirely with a CPaaS (Communications Platform as a Service) that handles telephony and streams audio via WebSocket to a serverless function.

```
┌──────────┐   PSTN    ┌──────────┐  WebSocket  ┌──────────────┐
│  Caller  │──────────►│  Twilio  │────────────►│  Edge Func   │
│  (Phone) │           │  /Vonage │             │  (OpenAI WS) │
└──────────┘           └──────────┘             └──────────────┘
```

### Option C: Asterisk in Cloud + Existing Bridge

Host an Asterisk PBX on a cloud VM and keep the Python/ARI bridge approach.

---

## 3. Detailed Cost Analysis

### 3.1 Option A — Cloud VPS (Recommended)

| Item | Monthly Cost | Notes |
|------|-------------|-------|
| **Cloud VPS (4 vCPU, 8GB RAM)** | £40–£80 | Hetzner, OVH, or DigitalOcean. Handles 50+ concurrent calls |
| **SIP Trunk Provider** | £20–£50 | Sipgate, Voipfone, or Andrews & Arnold. Includes DDI numbers |
| **OpenAI Realtime API** | £300–£1,500 | ~£0.06/min input + £0.24/min output. For 500–2000 calls/month @ avg 3 min |
| **Supabase (Lovable Cloud)** | £0 (included) | Already provisioned |
| **Photon Geocoding** | £0 | Free, self-hosted OSM data |
| **Domain + SSL** | £5 | For admin dashboard |
| **Monitoring (optional)** | £0–£30 | Grafana Cloud free tier or Uptime Robot |
| **TOTAL** | **£365–£1,665/month** | |

### 3.2 Option B — Fully Serverless (Twilio)

| Item | Monthly Cost | Notes |
|------|-------------|-------|
| **Twilio Phone Number** | £1–£3 | Per DDI |
| **Twilio Voice (inbound)** | £0.01/min | PSTN → Twilio |
| **Twilio Media Streams** | Included | WebSocket audio streaming |
| **Edge Function Compute** | £25–£100 | Extended execution for real-time WebSocket |
| **OpenAI Realtime API** | £300–£1,500 | Same as Option A |
| **Development Cost** | £5,000–£15,000 | One-off: rewrite audio pipeline for Twilio format |
| **TOTAL (recurring)** | **£330–£1,600/month** | |
| **TOTAL (one-off)** | **£5,000–£15,000** | |

### 3.3 Option C — Cloud Asterisk + Bridge

| Item | Monthly Cost | Notes |
|------|-------------|-------|
| **Cloud VPS (Asterisk)** | £30–£60 | Dedicated PBX server |
| **Cloud VPS (Bridge)** | £20–£40 | Python ARI bridge |
| **SIP Trunk** | £20–£50 | |
| **OpenAI Realtime API** | £300–£1,500 | |
| **TOTAL** | **£370–£1,650/month** | More complex, two servers to manage |

### 3.4 Cost Comparison Summary

| | Option A (VPS) | Option B (Serverless) | Option C (Asterisk) |
|---|---|---|---|
| Monthly cost | £365–£1,665 | £330–£1,600 | £370–£1,650 |
| Setup cost | £500–£2,000 | £5,000–£15,000 | £2,000–£5,000 |
| Complexity | Low | High | Medium |
| Time to deploy | 1–2 weeks | 8–14 weeks | 3–6 weeks |
| Risk | Low | Medium-High | Medium |

---

## 4. Infrastructure Requirements

### 4.1 Network Requirements

| Requirement | On-Premise (Current) | Cloud |
|-------------|---------------------|-------|
| Static IP | Required (router config) | Included with VPS |
| SIP ALG | Must be disabled on router | Not applicable |
| Port forwarding | UDP 5060 (SIP) + 10000-20000 (RTP) | Firewall rules on VPS |
| QoS prioritization | Manual router config | Cloud provider handles |
| NAT traversal | STUN + manual config | Simpler (public IP) |
| Bandwidth | ~100kbps per concurrent call | VPS has Gbps connectivity |
| Latency | Depends on broadband quality | Predictable (datacentre) |
| Uptime | Dependent on local power/internet | 99.9%+ SLA |

**Key advantage of cloud:** Eliminates the most common on-premise issues — NAT traversal failures, SIP ALG interference, broadband instability, power outages, and router misconfiguration.

### 4.2 Server Specifications

**Minimum cloud VPS for Option A:**

| Resource | Specification | Justification |
|----------|--------------|---------------|
| CPU | 4 vCPU (dedicated) | RTP playout requires precise 20ms timing |
| RAM | 8 GB | ~50MB per concurrent call + OS overhead |
| Storage | 40 GB SSD | Logs, binaries, certificates |
| OS | Ubuntu 22.04 LTS | .NET 8 runtime support |
| Network | 1 Gbps, static IPv4 | SIP registration + RTP streams |

### 4.3 Security

| Concern | On-Premise | Cloud |
|---------|-----------|-------|
| SIP authentication | SIP trunk credentials in local file | Same, stored as environment variables |
| OpenAI API key | Local appsettings.json | Environment variable / secrets manager |
| TLS for signalling | Depends on trunk provider | SIP-TLS (port 5061) recommended |
| SRTP for media | Optional | Recommended for compliance |
| SSH access | N/A | Key-based only, no password auth |
| Firewall | Router-dependent | UFW / cloud firewall (allow only SIP + RTP ports) |
| DDoS protection | None | Cloud provider basic protection included |

---

## 5. Required Code Changes

### 5.1 Changes for Option A (Cloud VPS — Recommended)

| Change | Effort | Description |
|--------|--------|-------------|
| **Remove Windows dependencies** | ✅ Already done | `AdaMain.Server` targets `net8.0` (cross-platform) |
| **Docker containerisation** | 2–3 days | Dockerfile + docker-compose already exist in project |
| **Health check endpoint** | 1 day | REST API for monitoring (currently TODO) |
| **Prometheus metrics** | 2–3 days | Call counts, latency, RTP packet loss (currently TODO) |
| **Log aggregation** | 1 day | journald → cloud logging (e.g., Loki, CloudWatch) |
| **Configuration management** | 1 day | Move from appsettings.json to environment variables (partially done) |
| **Auto-restart on crash** | ✅ Already done | systemd service with `Restart=always` |
| **TOTAL** | **~1–2 weeks** | |

### 5.2 Changes for Option B (Fully Serverless)

| Change | Effort | Description |
|--------|--------|-------------|
| **Replace SIPSorcery with Twilio SDK** | 3–4 weeks | Complete SIP/RTP stack replacement |
| **Rewrite audio pipeline** | 2–3 weeks | Twilio uses µ-law 8kHz, different framing |
| **Convert to serverless function** | 1–2 weeks | Long-running WebSocket in edge function |
| **Handle Twilio webhook auth** | 2 days | Signature validation |
| **Remove .NET dependency entirely** | 1 week | Rewrite in TypeScript/Deno for edge functions |
| **Testing & validation** | 2–3 weeks | Audio quality, latency, edge cases |
| **TOTAL** | **~10–14 weeks** | |

### 5.3 What Does NOT Need to Change (All Options)

These components are **already cloud-native** and require zero modification:

- ✅ OpenAI Realtime API integration
- ✅ Supabase database (callers, bookings, live_calls)
- ✅ Edge Functions (address-dispatch, geocoding, fare calculation)
- ✅ BSQD dispatch webhook integration
- ✅ Agent configuration (database-driven, managed via web dashboard)
- ✅ Caller history and CRM features
- ✅ Admin dashboard (Lovable web app)

---

## 6. Transitional Challenges & Risks

### 6.1 Audio Quality & Latency

| Challenge | Impact | Mitigation |
|-----------|--------|------------|
| **RTP timing precision** | Cloud VMs may have less precise CPU scheduling than dedicated hardware | Use dedicated vCPU (not shared), enable `WinMmTimer` equivalent on Linux (`timerfd`), test thoroughly |
| **Jitter** | Cloud networks can introduce variable latency | Current 400ms jitter buffer handles this; may need tuning |
| **Codec compatibility** | SIP trunk provider must support G.711 A-law (PCMA) | Verify before selecting provider; most UK providers support this |
| **One-way audio** | NAT/firewall misconfiguration can cause caller-can't-hear-Ada issues | Cloud eliminates NAT issues; simpler than on-premise |

### 6.2 SIP Trunk Migration

| Challenge | Impact | Mitigation |
|-----------|--------|------------|
| **Number porting** | Existing phone numbers must transfer to cloud-compatible SIP provider | Allow 2–4 weeks for UK number porting |
| **Trunk authentication** | Some providers use IP-based auth (must update to cloud IP) | Use credential-based auth instead |
| **Emergency services** | 999 routing requirements if applicable | Verify with provider; may need registered address |
| **Concurrent call limits** | Trunk capacity must match expected volume | Size appropriately; most providers offer elastic capacity |

### 6.3 Operational Risks During Transition

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| **Downtime during switchover** | Medium | Missed calls during transition | Run parallel for 1–2 weeks; DNS/SIP failover |
| **Audio quality regression** | Low-Medium | Customer complaints | A/B test with internal calls before go-live |
| **Provider-specific SIP quirks** | Medium | Registration failures or codec mismatches | Test with chosen provider in staging first |
| **OpenAI API cost spike** | Low | Unexpected bills | Set usage caps and alerts via OpenAI dashboard |
| **Data migration** | Low | N/A — database is already in cloud | No action needed |

### 6.4 Operational Differences

| Aspect | On-Premise | Cloud |
|--------|-----------|-------|
| **Physical access** | Walk up to the machine | SSH / remote only |
| **Troubleshooting** | Plug in monitor, check cables | Logs, metrics, remote diagnostics |
| **Updates** | Manual copy of files | CI/CD pipeline (Docker push → restart) |
| **Scaling** | Buy new hardware | Resize VPS or add instances |
| **Backup** | Manual | Automated snapshots |
| **Disaster recovery** | Difficult (hardware failure) | Spin up new VM in minutes |
| **Monitoring** | Desktop UI (WinForms) | Web dashboard + alerts |

---

## 7. Deployment Timeline

### Phase 1: Staging Environment (Week 1–2)
- Provision cloud VPS
- Deploy Ada .NET Server via Docker
- Configure SIP trunk (test number)
- Internal testing: make test calls, verify audio quality

### Phase 2: Parallel Running (Week 3–4)
- Run cloud and on-premise simultaneously
- Route test calls to cloud instance
- Monitor metrics: latency, call quality, completion rates
- Address any audio or stability issues

### Phase 3: Cutover (Week 5)
- Port production phone numbers to cloud SIP trunk
- Switch all traffic to cloud instance
- Keep on-premise as warm standby for 1 week
- Monitor closely

### Phase 4: Decommission On-Premise (Week 6+)
- Confirm cloud stability over 1–2 weeks
- Shut down on-premise server
- Update documentation and runbooks

---

## 8. Ongoing Management

### 8.1 Web-Based Admin Dashboard

The existing Lovable web dashboard already provides:
- ✅ Agent configuration (voice, personality, prompts)
- ✅ Live call monitoring (real-time transcripts)
- ✅ Booking history and caller CRM
- ✅ SIP trunk configuration

**Additional features needed for cloud management:**
- 🔲 Server health/status page
- 🔲 Call volume and cost analytics
- 🔲 Alert configuration (downtime, error rate spikes)
- 🔲 One-click restart / deploy

### 8.2 Monitoring & Alerting

Recommended monitoring stack (free/low-cost):

| Tool | Purpose | Cost |
|------|---------|------|
| **Uptime Robot** | Ping health endpoint every 60s | Free (50 monitors) |
| **Grafana Cloud** | Metrics dashboard (calls, latency, errors) | Free (up to 10k metrics) |
| **PagerDuty / Opsgenie** | Alert on-call engineer | From £10/month |
| **journald + Loki** | Log aggregation and search | Free (self-hosted) |

---

## 9. Recommendation

**We recommend Option A (Cloud VPS)** for the following reasons:

1. **Lowest risk** — minimal code changes, proven architecture
2. **Fastest deployment** — 2–4 weeks to production
3. **Lowest setup cost** — £500–£2,000 vs £5,000–£15,000 for serverless
4. **Eliminates on-premise pain points** — NAT, SIP ALG, power outages, broadband instability
5. **Preserves investment** — all existing code, testing, and tuning carries over
6. **Clear upgrade path** — can migrate to serverless later if needed

### Cost Summary (Option A)

| | Monthly | Annual |
|---|---|---|
| **Low volume** (500 calls/month) | ~£400 | ~£4,800 |
| **Medium volume** (1,500 calls/month) | ~£900 | ~£10,800 |
| **High volume** (3,000 calls/month) | ~£1,700 | ~£20,400 |

> *Note: 60–80% of cost is OpenAI API usage, which applies equally to all options.*

---

## 10. Appendix: Current Edge Function Inventory

These cloud functions are **already deployed and operational** — no migration needed:

| Function | Purpose |
|----------|---------|
| `taxi-realtime` | Main OpenAI Realtime WebSocket handler (10,232 lines) |
| `address-dispatch` | Gemini address resolution + Haversine fare calculation |
| `address-autocomplete` | Photon geocoder autocomplete |
| `taxi-booking-webhook` | BSQD dispatch callback handler |
| `caller-history-save` | Caller CRM data persistence |
| `taxi-chat` | Web chatbot text interface |
| `taxi-tts` / `taxi-stt` | Text-to-speech / speech-to-text utilities |
| `geocode` | Coordinate lookup |

---

*End of Report*
