// Ultra-resilient debug logger
document.title = 'BOOT...';
var _dbg = document.getElementById('debugLog');
// Debug panel hidden by default, shown on errors only
function dbg(msg){
  try {
    var d = document.getElementById('debugLog');
    if(d){ d.innerHTML += new Date().toLocaleTimeString() + ' ' + msg + '<br>'; d.scrollTop = d.scrollHeight; }
    document.title = msg.substring(0, 50);
  } catch(e){}
  console.log(msg);
}
window.onerror = function(msg, url, line, col, err) {
  if(_dbg) _dbg.style.display = 'block'; // show debug panel on error
  dbg('❌ ERROR L' + line + ': ' + msg);
  var pill = document.getElementById('mqttText');
  if(pill) pill.textContent = 'JS Error L' + line;
  return false;
};
dbg('[BOOT] Script starting...');
dbg('[BOOT] mqtt global: ' + (typeof mqtt) + ', L global: ' + (typeof L));
// ── MAP SETUP ──
const DRIVER_START = [52.4068, -1.5197]; // Coventry
const map = L.map('map').setView(DRIVER_START, 13);
L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', { attribution:'© OpenStreetMap', maxZoom:19 }).addTo(map);

const cabSvg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 58 36" width="46" height="28"><rect x="4" y="14" width="50" height="14" rx="3" fill="#000"/><path d="M18 14v-4c0-3 2-5 5-5h12c3 0 5 2 5 5v4h-22z" fill="#000"/><rect x="25" y="4" width="8" height="3" rx="1" fill="#FFD700" stroke="#000" stroke-width="0.5"/><text x="29" y="6.6" text-anchor="middle" font-size="2.2" font-weight="bold" fill="#000">TAXI</text><rect x="21" y="7" width="6" height="6" rx="1" fill="#fff"/><rect x="31" y="7" width="6" height="6" rx="1" fill="#fff"/><circle cx="15" cy="28" r="4" fill="#fff" stroke="#000" stroke-width="1"/><circle cx="43" cy="28" r="4" fill="#fff" stroke="#000" stroke-width="1"/></svg>`;
const driverIcon = L.divIcon({ html: cabSvg, iconSize:[46,28], iconAnchor:[23,14], className:'' });
const driverMarker = L.marker(DRIVER_START, { icon: driverIcon, zIndexOffset: 1000 }).addTo(map);

let driverLat = DRIVER_START[0], driverLng = DRIVER_START[1];
const jobMarkers = {};
let routeLines = [];

function pickupIcon(color='#F44336') {
  return L.divIcon({ className:'', iconSize:[24,36], iconAnchor:[12,36],
    html:`<svg width="24" height="36" viewBox="0 0 24 36"><path d="M12 0C5.4 0 0 5.4 0 12c0 9 12 24 12 24s12-15 12-24C24 5.4 18.6 0 12 0z" fill="${color}" stroke="#fff" stroke-width="1.5"/><circle cx="12" cy="11" r="5" fill="#fff"/></svg>`
  });
}
function dropoffIcon() {
  return L.divIcon({ className:'', iconSize:[24,36], iconAnchor:[12,36],
    html:`<svg width="24" height="36" viewBox="0 0 24 36"><path d="M12 0C5.4 0 0 5.4 0 12c0 9 12 24 12 24s12-15 12-24C24 5.4 18.6 0 12 0z" fill="#3b82f6" stroke="#fff" stroke-width="1.5"/><rect x="7" y="7" width="10" height="8" rx="1" fill="#fff"/></svg>`
  });
}

// ── DATA ──
const JOB_STATES = ['ASSIGNED','EN_ROUTE_TO_PICKUP','ARRIVED_PICKUP','EN_ROUTE_TO_DROPOFF','AT_DROPOFF','COMPLETED'];
const STATE_LABELS = { ASSIGNED:'Assigned', EN_ROUTE_TO_PICKUP:'On the Way', ARRIVED_PICKUP:'Arrived at Pickup', EN_ROUTE_TO_DROPOFF:'Passenger On Board', AT_DROPOFF:'At Dropoff', COMPLETED:'Completed' };
const STATE_BUTTONS = { ASSIGNED:'Navigate to Pickup', EN_ROUTE_TO_PICKUP:'Arrived at Pickup', ARRIVED_PICKUP:'Passenger On Board', EN_ROUTE_TO_DROPOFF:'Arrived at Dropoff', AT_DROPOFF:'End Job' };

let availableJobs = [];
let inboxJobs = [];
let historyJobs = [];
let selectedJobId = null;
let expandedConnectors = null;
let activeInboxJob = null;

// ── LOCAL STORAGE PERSISTENCE ──
const LS_KEY_AVAIL = 'bcu_demo_available_jobs';
const LS_KEY_INBOX = 'bcu_demo_inbox_jobs';
const LS_KEY_HISTORY = 'bcu_demo_history_jobs';

function saveJobsToStorage() {
  try {
    localStorage.setItem(LS_KEY_AVAIL, JSON.stringify(availableJobs));
    localStorage.setItem(LS_KEY_INBOX, JSON.stringify(inboxJobs));
    localStorage.setItem(LS_KEY_HISTORY, JSON.stringify(historyJobs));
  } catch(e) { dbg('[LS] Save error: ' + e.message); }
}

function loadJobsFromStorage() {
  try {
    var a = localStorage.getItem(LS_KEY_AVAIL);
    var i = localStorage.getItem(LS_KEY_INBOX);
    var h = localStorage.getItem(LS_KEY_HISTORY);
    if (a) availableJobs = JSON.parse(a);
    if (i) inboxJobs = JSON.parse(i);
    if (h) historyJobs = JSON.parse(h);
    dbg('[LS] Loaded ' + availableJobs.length + ' available, ' + inboxJobs.length + ' inbox, ' + historyJobs.length + ' history jobs');
  } catch(e) { dbg('[LS] Load error: ' + e.message); }
}

loadJobsFromStorage();
let msgPanelOpen = false;
let messages = [];
let totalEarnings = 0;

// ── MOCK JOB GENERATOR ──
const ADDRESSES = [
  { name:'Coventry Station', lat:52.4003, lng:-1.5131 },
  { name:'University Hospital', lat:52.4210, lng:-1.4461 },
  { name:'Ricoh Arena', lat:52.4488, lng:-1.4958 },
  { name:'War Memorial Park', lat:52.3946, lng:-1.5059 },
  { name:'Belgrade Theatre', lat:52.4082, lng:-1.5107 },
  { name:'Coventry Cathedral', lat:52.4087, lng:-1.5073 },
  { name:'Westwood Business Park', lat:52.3821, lng:-1.5688 },
  { name:'Tile Hill Station', lat:52.3933, lng:-1.5813 },
  { name:'Whitley Business Park', lat:52.3903, lng:-1.4602 },
  { name:'Kenilworth Road', lat:52.3544, lng:-1.5697 },
  { name:'Binley Mega', lat:52.3976, lng:-1.4558 },
  { name:'Walsgrave Hospital', lat:52.4227, lng:-1.4373 },
];

function haversine(lat1,lng1,lat2,lng2){
  const R=6371e3,p=Math.PI/180;
  const a=Math.sin((lat2-lat1)*p/2)**2+Math.cos(lat1*p)*Math.cos(lat2*p)*Math.sin((lng2-lng1)*p/2)**2;
  return R*2*Math.atan2(Math.sqrt(a),Math.sqrt(1-a));
}

function generateJob(){
  const p = ADDRESSES[Math.floor(Math.random()*ADDRESSES.length)];
  let d; do { d = ADDRESSES[Math.floor(Math.random()*ADDRESSES.length)]; } while(d.name===p.name);
  const dist = haversine(driverLat,driverLng,p.lat,p.lng);
  const fare = (5 + Math.random()*25).toFixed(2);
  const notes = ['','Gate code 4321','Wheelchair access needed','2 suitcases','Airport transfer','VIP passenger'][Math.floor(Math.random()*6)];
  const tags = [];
  if(Math.random()>.7) tags.push('airport');
  if(Math.random()>.85) tags.push('wheelchair');
  if(Math.random()>.5) tags.push('asap');
  return {
    jobId: 'J'+Date.now().toString(36).toUpperCase(),
    createdAt: new Date().toISOString(),
    pickup: { lat:p.lat, lng:p.lng, address:p.name },
    dropoff: { lat:d.lat, lng:d.lng, address:d.name },
    pickupTime: tags.includes('asap')?'ASAP': new Date(Date.now()+Math.random()*3600000).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'}),
    passenger: { name:['Smith','Jones','Williams','Brown','Taylor','Wilson'][Math.floor(Math.random()*6)], phoneMasked:'+44*******' },
    notes, tags,
    pricing: { estimate:parseFloat(fare), currency:'GBP' },
    distanceFromDriver: Math.round(dist),
    etaToPickup: Math.round(dist/500*60),
    bidStatus: 'none',
  };
}

// ── RENDERING ──
function renderAvailableJobs(){
  saveJobsToStorage();
  availableJobs.sort((a,b)=>a.distanceFromDriver-b.distanceFromDriver);
  const el = document.getElementById('availableList');
  document.getElementById('availCount').textContent = availableJobs.length;
  if(!availableJobs.length){ el.innerHTML='<div class="empty-state"><div class="icon">🔍</div>Waiting for jobs…</div>'; return; }
  el.innerHTML = availableJobs.map(j=>{
    const km = (j.distanceFromDriver/1000).toFixed(1);
    const sel = j.jobId===selectedJobId?'selected':'';
    const pending = j.bidStatus==='pending'?'bid-pending':'';
    const conns = getConnectingJobs(j);
    const connCount = conns.length;
    const expanded = expandedConnectors===j.jobId;
    const connHTML = expanded && connCount ? `<div class="card-connectors">
      <div style="font-size:11px;color:var(--cyan);font-weight:700;margin-bottom:4px">🔗 Connecting jobs near dropoff:</div>
      ${conns.map(c=>{
        const ckm = (c.distFromDropoff/1000).toFixed(1);
        return `<div class="conn-mini" onclick="event.stopPropagation();previewConnector('${c.jobId}','${j.jobId}')">
          <div style="display:flex;justify-content:space-between">
            <span style="font-size:11px;font-weight:600">📍 ${c.pickup.address}</span>
            <span style="font-size:10px;color:var(--cyan)">${ckm}km</span>
          </div>
          <div style="font-size:10px;color:var(--muted)">🏁 ${c.dropoff.address} · <span style="color:var(--green);font-weight:700">£${c.pricing.estimate.toFixed(2)}</span>${c.savedKm>0?` · <span style="color:var(--gold)">Save ${c.savedKm}km</span>`:''}</div>
        </div>`;
      }).join('')}
      ${connCount?`<div style="font-size:11px;color:var(--gold);font-weight:700;margin-top:4px">💰 Chain total: £${(j.pricing.estimate+conns.reduce((s,c)=>s+c.pricing.estimate,0)).toFixed(2)}</div>`:''}
    </div>` : '';
    return `<div class="job-card ${sel} ${pending}" onclick="selectJob('${j.jobId}')">
      <div class="jc-top">
        <div class="jc-pickup">${j.pickup.address}</div>
        <div class="jc-dist">${km}<small> km</small></div>
      </div>
      <div class="jc-dropoff">${j.dropoff.address}</div>
      <div class="jc-meta">
        <span class="jc-fare">£${j.pricing.estimate.toFixed(2)}</span>
        <span class="jc-time">${j.pickupTime}</span>
        <span>ETA ${j.etaToPickup}min</span>
      </div>
      ${j.notes?`<div class="jc-notes">📝 ${j.notes}</div>`:''}
      <div class="jc-tags">${j.tags.map(t=>`<span class="tag tag-${t}">${t.toUpperCase()}</span>`).join('')}</div>
      <div class="jc-actions">
        <button class="btn btn-preview" onclick="event.stopPropagation();previewJob('${j.jobId}')">👁 Preview</button>
        <button class="btn btn-connect" onclick="event.stopPropagation();toggleConnectors('${j.jobId}')">🔗 ${connCount}${connCount?'':'  '}</button>
        <button class="btn btn-bid" ${j.bidStatus!=='none'?'disabled':''} onclick="event.stopPropagation();bidOnJob('${j.jobId}')">
          ${j.bidStatus==='pending'?'⏳ Pending…':'⚡ BID'}
        </button>
      </div>
      ${connHTML}
    </div>`;
  }).join('');
}

function selectJob(id){
  selectedJobId = id;
  const j = availableJobs.find(x=>x.jobId===id);
  if(j) showJobOnMap(j);
  renderAvailableJobs();
}


// ── TTS ANNOUNCEMENTS ──
let ttsEnabled = true;
function announceNewJob(j){
  if(!ttsEnabled || !('speechSynthesis' in window)) return;
  const km = (j.distanceFromDriver/1000).toFixed(1);
  const text = `New job available. ${j.pickup.address} to ${j.dropoff.address}. ${km} kilometres away. £${j.pricing.estimate.toFixed(0)}.`;
  const utterance = new SpeechSynthesisUtterance(text);
  utterance.rate = 1.1;
  utterance.pitch = 1.0;
  utterance.volume = 0.8;
  const voices = speechSynthesis.getVoices();
  const britishVoice = voices.find(v=>v.lang==='en-GB') || voices.find(v=>v.lang.startsWith('en'));
  if(britishVoice) utterance.voice = britishVoice;
  speechSynthesis.speak(utterance);
}

function announceBidResult(won, j){
  if(!ttsEnabled || !('speechSynthesis' in window)) return;
  const text = won ? `Bid won! ${j.pickup.address} to ${j.dropoff.address}.` : `Bid lost for ${j.pickup.address}.`;
  const utterance = new SpeechSynthesisUtterance(text);
  utterance.rate = 1.1;
  utterance.volume = 0.8;
  const voices = speechSynthesis.getVoices();
  const britishVoice = voices.find(v=>v.lang==='en-GB') || voices.find(v=>v.lang.startsWith('en'));
  if(britishVoice) utterance.voice = britishVoice;
  speechSynthesis.speak(utterance);
}

// Pre-load voices
if('speechSynthesis' in window){ speechSynthesis.getVoices(); window.speechSynthesis.onvoiceschanged=()=>speechSynthesis.getVoices(); }

function toggleTTS(){
  ttsEnabled=!ttsEnabled;
  document.getElementById('ttsPill').textContent=ttsEnabled?'🔊 TTS On':'🔇 TTS Off';
  document.getElementById('ttsPill').style.borderColor=ttsEnabled?'var(--green)':'#555';
  if(!ttsEnabled) speechSynthesis.cancel();
}


async function fetchRoute(coords){
  try {
    const res = await fetch(`https://router.project-osrm.org/route/v1/driving/${coords}?overview=full&geometries=geojson`);
    const data = await res.json();
    if(data.routes && data.routes[0]) return { geom: data.routes[0].geometry.coordinates.map(c=>[c[1],c[0]]), duration: data.routes[0].duration, distance: data.routes[0].distance };
  } catch(e){}
  return null;
}

let activeRouteJobId = null;
let routeRequestId = 0;

async function showJobOnMap(j){
  const thisRequest = ++routeRequestId;
  activeRouteJobId = j.jobId;
  clearMapOverlays();
  jobMarkers.pickup = L.marker([j.pickup.lat,j.pickup.lng],{icon:pickupIcon('#22c55e')}).addTo(map).bindPopup(`<b>Pickup</b><br>${j.pickup.address}`);
  jobMarkers.dropoff = L.marker([j.dropoff.lat,j.dropoff.lng],{icon:dropoffIcon()}).addTo(map).bindPopup(`<b>Dropoff</b><br>${j.dropoff.address}`);

  routeLines.push(L.polyline([[driverLat,driverLng],[j.pickup.lat,j.pickup.lng]],{color:'#3b82f6',weight:3,dashArray:'10,6',opacity:0.4}).addTo(map));
  routeLines.push(L.polyline([[j.pickup.lat,j.pickup.lng],[j.dropoff.lat,j.dropoff.lng]],{color:'#FFD700',weight:3,dashArray:'10,6',opacity:0.4}).addTo(map));
  map.fitBounds(L.featureGroup(routeLines).getBounds().pad(0.15));

  const [toPickup, toDrop] = await Promise.all([
    fetchRoute(`${driverLng},${driverLat};${j.pickup.lng},${j.pickup.lat}`),
    fetchRoute(`${j.pickup.lng},${j.pickup.lat};${j.dropoff.lng},${j.dropoff.lat}`)
  ]);

  if(thisRequest !== routeRequestId) return;

  routeLines.forEach(l=>map.removeLayer(l));
  routeLines=[];

  if(toPickup){
    routeLines.push(L.polyline(toPickup.geom,{color:'#3b82f6',weight:5,opacity:0.85}).addTo(map));
    const dur = Math.round(toPickup.duration/60);
    const dist = (toPickup.distance/1000).toFixed(1);
    toast(`🛣️ To pickup: ${dist} km · ${dur} min`,'info');
  } else {
    routeLines.push(L.polyline([[driverLat,driverLng],[j.pickup.lat,j.pickup.lng]],{color:'#3b82f6',weight:4,dashArray:'10,6'}).addTo(map));
  }
  if(toDrop){
    routeLines.push(L.polyline(toDrop.geom,{color:'#FFD700',weight:5,opacity:0.9}).addTo(map));
  } else {
    routeLines.push(L.polyline([[j.pickup.lat,j.pickup.lng],[j.dropoff.lat,j.dropoff.lng]],{color:'#FFD700',weight:4,dashArray:'10,6'}).addTo(map));
  }

  map.fitBounds(L.featureGroup(routeLines).getBounds().pad(0.15));
}

function clearMapOverlays(force){
  Object.entries(jobMarkers).forEach(([k,m])=>{
    if(!k.startsWith('avail_')) map.removeLayer(m);
    if(!k.startsWith('avail_')) delete jobMarkers[k];
  });
  routeLines.forEach(l=>map.removeLayer(l));
  routeLines=[];
}

function previewJob(id){
  const j = availableJobs.find(x=>x.jobId===id);
  if(j) showJobOnMap(j);
}

function getConnectingJobs(j){
  return availableJobs.filter(x=>x.jobId!==j.jobId).map(x=>{
    const distFromDropoff = haversine(j.dropoff.lat,j.dropoff.lng,x.pickup.lat,x.pickup.lng);
    const deadMiles = haversine(j.dropoff.lat,j.dropoff.lng,driverLat,driverLng);
    const savedKm = Math.max(0,((deadMiles-distFromDropoff)/1000)).toFixed(1);
    return {...x, distFromDropoff, savedKm:parseFloat(savedKm)};
  }).filter(x=>x.distFromDropoff<5000).sort((a,b)=>a.distFromDropoff-b.distFromDropoff).slice(0,5);
}

function renderConnectorsHTML(j, context){
  const conns = getConnectingJobs(j);
  if(!conns.length) return '<div style="color:var(--muted);font-size:12px;margin-top:8px">No connecting jobs nearby</div>';
  return `<div class="connectors-section">
    <div class="connectors-title">🔗 Connecting Jobs (near dropoff)</div>
    ${conns.map(c=>{
      const km = (c.distFromDropoff/1000).toFixed(1);
      return `<div class="conn-card" onclick="previewConnector('${c.jobId}','${j.jobId}')">
        <div class="conn-top">
          <span class="conn-pickup">📍 ${c.pickup.address}</span>
          <span class="conn-dist">${km} km from dropoff</span>
        </div>
        <div class="conn-dropoff">🏁 ${c.dropoff.address}</div>
        <div class="conn-meta">
          <span class="conn-fare">£${c.pricing.estimate.toFixed(2)}</span>
          <span>${c.pickupTime}</span>
          ${c.savedKm>0?`<span class="conn-savings">Save ${c.savedKm} km dead miles</span>`:''}
          <button class="conn-bid" onclick="event.stopPropagation();bidOnJob('${c.jobId}')" ${c.bidStatus!=='none'?'disabled':''}>${c.bidStatus==='pending'?'⏳':'⚡ BID'}</button>
        </div>
      </div>`;
    }).join('')}
  </div>`;
}

function previewConnector(connId, parentId){
  const parent = [...availableJobs,...inboxJobs].find(x=>x.jobId===parentId);
  const conn = availableJobs.find(x=>x.jobId===connId);
  if(!parent||!conn) return;
  clearMapOverlays();
  jobMarkers.pickup = L.marker([parent.pickup.lat,parent.pickup.lng],{icon:pickupIcon('#22c55e')}).addTo(map).bindPopup('Current Pickup');
  jobMarkers.dropoff = L.marker([parent.dropoff.lat,parent.dropoff.lng],{icon:dropoffIcon()}).addTo(map).bindPopup('Current Dropoff');
  jobMarkers.connPickup = L.marker([conn.pickup.lat,conn.pickup.lng],{icon:pickupIcon('#06b6d4')}).addTo(map).bindPopup('Next Pickup');
  jobMarkers.connDropoff = L.marker([conn.dropoff.lat,conn.dropoff.lng],{icon:dropoffIcon()}).addTo(map).bindPopup('Next Dropoff');
  const line = L.polyline([
    [driverLat,driverLng],[parent.pickup.lat,parent.pickup.lng],[parent.dropoff.lat,parent.dropoff.lng],
    [conn.pickup.lat,conn.pickup.lng],[conn.dropoff.lat,conn.dropoff.lng]
  ],{color:'#06b6d4',weight:4,dashArray:'8,6'}).addTo(map);
  routeLines.push(line);
  map.fitBounds(line.getBounds().pad(0.15));
}

function toggleConnectors(id){
  expandedConnectors = expandedConnectors===id ? null : id;
  renderAvailableJobs();
}

function showConnectors(id){ toggleConnectors(id); }

// ── BIDDING ──
function bidOnJob(id){
  const j = availableJobs.find(x=>x.jobId===id);
  if(!j) return;
  j.bidStatus = 'pending';
  renderAvailableJobs();
  toast('⚡ Bid submitted — waiting…','info');
  setTimeout(()=>{
    if(Math.random()>.3){
      j.bidStatus = 'won';
      availableJobs = availableJobs.filter(x=>x.jobId!==id);
      j.state = 'ASSIGNED';
      inboxJobs.push(j);
      document.getElementById('inboxCount').textContent = inboxJobs.length;
      toast('🎉 You won the job!','success');
      announceBidResult(true, j);
      renderAvailableJobs();
      switchTab('inbox');
      showInboxJob(j);
    } else {
      j.bidStatus = 'none';
      availableJobs = availableJobs.filter(x=>x.jobId!==id);
      toast('❌ Another driver selected','error');
      announceBidResult(false, j);
      renderAvailableJobs();
    }
  }, 1500+Math.random()*2000);
}

// ── INBOX ──
function showInboxJob(j){
  activeInboxJob = j;
  messages = [];
  const el = document.getElementById('inboxDetail');
  document.getElementById('availableList').style.display='none';
  document.getElementById('historyList').style.display='none';
  el.classList.add('visible');
  renderInboxDetail();
  showJobOnMap(j);
}

function renderInboxDetail(){
  const j = activeInboxJob;
  if(!j) return;
  const stateIdx = JOB_STATES.indexOf(j.state);
  const el = document.getElementById('inboxDetail');
  el.innerHTML = `
    <div class="inbox-back" onclick="backToList()">← Back to list</div>
    <div class="id-header">
      <h3>${j.pickup.address} → ${j.dropoff.address}</h3>
      <div class="id-passenger">👤 ${j.passenger.name} · £${j.pricing.estimate.toFixed(2)} · ${j.pickupTime}</div>
      ${j.notes?`<div class="jc-notes" style="margin-top:4px">📝 ${j.notes}</div>`:''}
    </div>
    <div class="timeline">
      ${JOB_STATES.filter(s=>s!=='COMPLETED').map((s,i)=>{
        const cls = i<stateIdx?'done':i===stateIdx?'current':'';
        return `<div class="tl-step ${cls}"><div class="tl-dot"></div><div class="tl-label">${STATE_LABELS[s]}</div></div>`;
      }).join('')}
    </div>
    ${renderConnectorsHTML(j,'inbox')}
    ${j.state!=='COMPLETED'?`<button class="primary-action" onclick="advanceState()">${STATE_BUTTONS[j.state]||'Done'}</button>`:''}
    <div class="secondary-actions">
      <button class="btn-secondary btn-msg" onclick="toggleMsgPanel()">💬 Message</button>
      <button class="btn-secondary btn-cancel" onclick="cancelJob()">✖ Cancel</button>
    </div>
    <div class="msg-panel ${msgPanelOpen?'visible':''}" id="msgPanel">
      <div style="display:flex;justify-content:space-between;align-items:center">
        <b style="color:var(--cyan)">Message Passenger</b>
        <span style="cursor:pointer" onclick="toggleMsgPanel()">✕</span>
      </div>
      <div class="msg-thread" id="msgThread">${messages.map(m=>`<div class="msg-bubble ${m.from==='driver'?'msg-driver':'msg-pax'}">${m.text}</div>`).join('')}</div>
      <div class="msg-presets">
        <div class="msg-preset" onclick="sendPreset(this.textContent)">I'm on my way</div>
        <div class="msg-preset" onclick="sendPreset(this.textContent)">I've arrived</div>
        <div class="msg-preset" onclick="sendPreset(this.textContent)">Where are you?</div>
        <div class="msg-preset" onclick="sendPreset(this.textContent)">Please come to pickup</div>
        <div class="msg-preset" onclick="sendPreset(this.textContent)">Running 5 min late</div>
      </div>
    </div>
  `;
}

function advanceState(){
  const j = activeInboxJob;
  if(!j) return;
  const idx = JOB_STATES.indexOf(j.state);
  if(idx<JOB_STATES.length-2){
    j.state = JOB_STATES[idx+1];
    toast(`✅ ${STATE_LABELS[j.state]}`,'success');
    renderInboxDetail();
    if(j.state==='EN_ROUTE_TO_PICKUP') document.getElementById('statusText').textContent='Busy';
  } else {
    j.state = 'COMPLETED';
    totalEarnings += j.pricing.estimate;
    document.getElementById('earningsTotal').textContent = totalEarnings.toFixed(2);
    inboxJobs = inboxJobs.filter(x=>x.jobId!==j.jobId);
    historyJobs.push(j);
    document.getElementById('inboxCount').textContent = inboxJobs.length;
    showCompletion(j);
  }
}

function cancelJob(){
  if(!activeInboxJob) return;
  toast('❌ Job cancelled','error');
  inboxJobs = inboxJobs.filter(x=>x.jobId!==activeInboxJob.jobId);
  document.getElementById('inboxCount').textContent = inboxJobs.length;
  activeInboxJob = null;
  backToList();
}

function toggleMsgPanel(){ msgPanelOpen=!msgPanelOpen; renderInboxDetail(); }

function sendPreset(text){
  messages.push({from:'driver',text});
  toast('💬 Message sent','info');
  renderInboxDetail();
  setTimeout(()=>{ messages.push({from:'passenger',text:'OK, thanks!'}); renderInboxDetail(); },2000);
}

function showCompletion(j){
  document.getElementById('completionSummary').innerHTML=`
    <div><span>Pickup</span><span>${j.pickup.address}</span></div>
    <div><span>Dropoff</span><span>${j.dropoff.address}</span></div>
    <div><span>Fare</span><span style="color:var(--green)">£${j.pricing.estimate.toFixed(2)}</span></div>
    <div><span>Total Earnings</span><span style="color:var(--gold)">£${totalEarnings.toFixed(2)}</span></div>
  `;
  document.getElementById('completionModal').classList.add('visible');
}

function closeCompletion(){
  document.getElementById('completionModal').classList.remove('visible');
  activeInboxJob=null;
  document.getElementById('statusText').textContent='Online';
  switchTab('available');
}

function backToList(){
  document.getElementById('inboxDetail').classList.remove('visible');
  document.getElementById('availableList').style.display='';
  document.getElementById('historyList').style.display='none';
  activeInboxJob=null;
  msgPanelOpen=false;
  clearMapOverlays();
  map.setView(DRIVER_START,13);
  renderAvailableJobs();
}

// ── SETTINGS PANEL ──
var jobRangeMiles = parseFloat(localStorage.getItem('bcu_demo_job_range') || '10');

function renderSettings(){
  var el = document.getElementById('settingsPanel');
  var selType = localStorage.getItem('bcu_demo_vehicle_type') || 'Saloon';
  var vtypes = ['Saloon','Estate','MPV','Executive','Minibus'];
  el.innerHTML = `
    <div class="settings-section">
      <h3>👤 Driver Profile</h3>
      <div class="settings-field">
        <label>Driver ID</label>
        <input type="text" id="setDriverId" value="${DRIVER_ID}" maxlength="30" autocomplete="off"/>
      </div>
      <div class="settings-field">
        <label>Full Name</label>
        <input type="text" id="setDriverName" value="${DRIVER_NAME}" maxlength="60" autocomplete="name"/>
      </div>
      <div class="settings-field">
        <label>Phone Number</label>
        <input type="tel" id="setDriverPhone" value="${DRIVER_PHONE}" maxlength="20" autocomplete="tel"/>
      </div>
    </div>
    <div class="settings-section">
      <h3>🚗 Vehicle</h3>
      <div class="settings-field">
        <label>Registration Plate</label>
        <input type="text" id="setVehicleReg" value="${VEHICLE_REG}" maxlength="10" style="text-transform:uppercase" autocomplete="off"/>
      </div>
      <div class="settings-field">
        <label>Vehicle Type</label>
        <div class="settings-vehicle-grid" id="setVehicleType">
          ${vtypes.map(function(v){ return '<div class="settings-vtype'+(v===selType?' selected':'')+'" data-type="'+v+'" onclick="selectSettingsVehicle(this)"><div class="icon">'+(v==='Saloon'?'🚗':v==='Estate'?'🚙':v==='MPV'?'🚐':v==='Executive'?'🏎️':'🚌')+'</div>'+v+'</div>'; }).join('')}
        </div>
      </div>
    </div>
    <div class="settings-section">
      <h3>📍 Job Range</h3>
      <div class="settings-field">
        <label>Show jobs within <span class="settings-range-value" id="rangeLabel">${jobRangeMiles}</span> miles</label>
        <input type="range" id="setJobRange" min="1" max="50" step="1" value="${jobRangeMiles}" oninput="document.getElementById('rangeLabel').textContent=this.value"/>
      </div>
    </div>
    <button class="settings-save" onclick="saveSettings()">💾 Save Settings</button>
  `;
}

function selectSettingsVehicle(el){
  document.querySelectorAll('.settings-vtype').forEach(function(o){ o.classList.remove('selected'); });
  el.classList.add('selected');
}

function saveSettings(){
  var newId = document.getElementById('setDriverId').value.trim();
  var newName = document.getElementById('setDriverName').value.trim();
  var newPhone = document.getElementById('setDriverPhone').value.trim();
  var newReg = document.getElementById('setVehicleReg').value.trim().toUpperCase();
  var newType = (document.querySelector('.settings-vtype.selected') || {}).dataset;
  var newVehicle = newType ? newType.type : 'Saloon';
  var newRange = parseInt(document.getElementById('setJobRange').value) || 10;

  if(!newId || !/^[a-zA-Z0-9_-]+$/.test(newId)){ toast('❌ Valid Driver ID is required','error'); return; }
  if(!newName || newName.length < 2){ toast('❌ Name is required','error'); return; }
  if(!newReg || newReg.length < 2){ toast('❌ Registration is required','error'); return; }

  // Check if driver ID changed — need to reconnect MQTT
  var idChanged = newId !== DRIVER_ID;

  DRIVER_ID = newId;
  DRIVER_NAME = newName;
  DRIVER_PHONE = newPhone;
  VEHICLE_REG = newReg;
  VEHICLE_TYPE = newVehicle;
  jobRangeMiles = newRange;

  localStorage.setItem('bcu_demo_driver_id', DRIVER_ID);
  localStorage.setItem('bcu_demo_driver_name', DRIVER_NAME);
  localStorage.setItem('bcu_demo_driver_phone', DRIVER_PHONE);
  localStorage.setItem('bcu_demo_vehicle_reg', VEHICLE_REG);
  localStorage.setItem('bcu_demo_vehicle_type', VEHICLE_TYPE);
  localStorage.setItem('bcu_demo_job_range', jobRangeMiles.toString());

  document.getElementById('driverIdLabel').textContent = DRIVER_NAME + ' (' + DRIVER_ID + ') • ' + VEHICLE_REG;
  toast('✅ Settings saved!','success');

  if(idChanged && mqttClient){
    dbg('[SETTINGS] Driver ID changed, reconnecting MQTT...');
    mqttClient.end(true);
    mqttRetryCount = 0;
    setTimeout(connectMqtt, 500);
  } else {
    publishDriverStatus();
  }
}

// ── TABS ──
function switchTab(tab){
  document.querySelectorAll('.tab').forEach(t=>t.classList.remove('active'));
  if(tab==='available'){
    document.getElementById('tabAvail').classList.add('active');
    document.getElementById('availableList').style.display='';
    document.getElementById('historyList').style.display='none';
    document.getElementById('settingsPanel').style.display='none';
    document.getElementById('inboxDetail').classList.remove('visible');
    renderAvailableJobs();
  } else if(tab==='inbox'){
    document.getElementById('tabInbox').classList.add('active');
    document.getElementById('availableList').style.display='none';
    document.getElementById('historyList').style.display='none';
    document.getElementById('settingsPanel').style.display='none';
    if(inboxJobs.length) showInboxJob(inboxJobs[0]);
    else { document.getElementById('inboxDetail').classList.add('visible');document.getElementById('inboxDetail').innerHTML='<div class="empty-state"><div class="icon">📥</div>No active jobs</div>'; }
  } else if(tab==='history'){
    document.getElementById('tabHistory').classList.add('active');
    document.getElementById('availableList').style.display='none';
    document.getElementById('inboxDetail').classList.remove('visible');
    document.getElementById('historyList').style.display='';
    document.getElementById('settingsPanel').style.display='none';
    renderHistory();
  } else if(tab==='settings'){
    document.getElementById('tabSettings').classList.add('active');
    document.getElementById('availableList').style.display='none';
    document.getElementById('inboxDetail').classList.remove('visible');
    document.getElementById('historyList').style.display='none';
    document.getElementById('settingsPanel').style.display='';
    renderSettings();
  }
}

function renderHistory(){
  const el = document.getElementById('historyList');
  if(!historyJobs.length){ el.innerHTML='<div class="empty-state"><div class="icon">📋</div>No completed jobs yet</div>'; return; }
  el.innerHTML = historyJobs.map(j=>`<div class="job-card"><div class="jc-top"><div class="jc-pickup">${j.pickup.address}</div><div class="jc-dist" style="color:var(--green)">£${j.pricing.estimate.toFixed(2)}</div></div><div class="jc-dropoff">${j.dropoff.address}</div></div>`).join('');
}

// ── TOAST ──
function toast(msg,type='info'){
  const c=document.getElementById('toasts');
  const t=document.createElement('div');
  t.className=`toast toast-${type}`;t.textContent=msg;
  c.appendChild(t);
  setTimeout(()=>t.remove(),3000);
}

// ── DRIVER REGISTRATION ──
var DRIVER_ID = localStorage.getItem('bcu_demo_driver_id');
if (!DRIVER_ID || !/^[a-zA-Z0-9_-]+$/.test(DRIVER_ID)) {
  dbg('⚠️ No driver ID found, redirecting to setup...');
  DRIVER_ID = 'TEMP_' + Date.now();
  setTimeout(function(){ window.location.href = 'driver-setup.html'; }, 100);
}

var DRIVER_NAME = localStorage.getItem('bcu_demo_driver_name') || DRIVER_ID;
var DRIVER_PHONE = localStorage.getItem('bcu_demo_driver_phone') || '';
var VEHICLE_REG = localStorage.getItem('bcu_demo_vehicle_reg') || '';
var VEHICLE_TYPE = localStorage.getItem('bcu_demo_vehicle_type') || 'Saloon';

document.getElementById('driverIdLabel').textContent = DRIVER_NAME + ' (' + DRIVER_ID + ') • ' + VEHICLE_REG;

let driverPresence = localStorage.getItem('bcu_demo_driver_presence') || 'available';

function cycleDriverStatus() {
  const states = ['available', 'busy', 'offline'];
  const idx = (states.indexOf(driverPresence) + 1) % states.length;
  driverPresence = states[idx];
  localStorage.setItem('bcu_demo_driver_presence', driverPresence);
  updateDriverStatusUI();
  publishDriverStatus();
}

function updateDriverStatusUI() {
  const pill = document.getElementById('driverStatus');
  const text = document.getElementById('statusText');
  pill.classList.remove('status-online');
  pill.style.background = '';
  pill.style.color = '';
  if (driverPresence === 'available') {
    pill.classList.add('status-online');
    text.textContent = 'Available';
  } else if (driverPresence === 'busy') {
    pill.style.background = 'rgba(6,182,212,.15)';
    pill.style.color = 'var(--cyan)';
    text.textContent = 'Busy';
  } else {
    pill.style.background = 'rgba(128,128,128,.15)';
    pill.style.color = '#888';
    text.textContent = 'Offline';
  }
}
updateDriverStatusUI();

function publishDriverStatus() {
  if (!mqttClient || !mqttConnected) return;
  const payload = {
    driver: DRIVER_ID,
    status: driverPresence,
    name: DRIVER_NAME,
    phone: DRIVER_PHONE,
    registration: VEHICLE_REG,
    vehicle: VEHICLE_TYPE,
    lat: driverLat,
    lng: driverLng,
    ts: Date.now()
  };
  mqttClient.publish(`drivers/${DRIVER_ID}/status`, JSON.stringify(payload));
  console.log('[MQTT] Published status:', driverPresence);
}

// ── REAL MQTT CONNECTION ──
const MQTT_BROKERS = [
  'wss://broker.hivemq.com:8884/mqtt',
  'wss://test.mosquitto.org:8081'
];
let currentBrokerIdx = 0;
let MQTT_BROKER = MQTT_BROKERS[0];
let mqttClient = null;
let mqttConnected = false;
let mqttRetryCount = 0;
const otherDrivers = {};
const driverStatus = {};
const chatMessages = { group: [] };
let currentView = 'jobs';

function mqttJobFromPayload(data){
  const pickupLat = parseFloat(data.lat || data.pickupLat) || 0;
  const pickupLng = parseFloat(data.lng || data.pickupLng) || 0;
  const dropoffLat = parseFloat(data.dropoffLat || data.dropofflat) || 0;
  const dropoffLng = parseFloat(data.dropoffLng || data.dropofflon) || 0;
  const jobId = data.jobId || data.job || data.id || ('MQ'+Date.now().toString(36).toUpperCase());
  // Skip if already in inbox (being worked on)
  if(inboxJobs.find(j=>j.jobId===jobId)) return null;
  // Remove existing available job with same ID (will be overwritten)
  var existingIdx = availableJobs.findIndex(j=>j.jobId===jobId);
  if(existingIdx !== -1) {
    availableJobs.splice(existingIdx, 1);
    if(jobMarkers['avail_'+jobId]) { map.removeLayer(jobMarkers['avail_'+jobId]); delete jobMarkers['avail_'+jobId]; }
    dbg('[JOB] Overwriting existing job ' + jobId);
  }

  const dist = haversine(driverLat, driverLng, pickupLat, pickupLng);
  const fare = parseFloat(data.fare) || (5+Math.random()*20);
  const notes = data.notes || data.special_instructions || '';
  const tags = [];
  if((data.temp1||'').toLowerCase().includes('asap') || (data.pickupTime||'').toLowerCase().includes('asap')) tags.push('asap');
  if((data.notes||'').toLowerCase().includes('airport') || (data.temp2||'')==='airport') tags.push('airport');
  if((data.notes||'').toLowerCase().includes('wheelchair')) tags.push('wheelchair');

  return {
    jobId,
    createdAt: new Date().toISOString(),
    pickup: { lat: pickupLat, lng: pickupLng, address: data.pickupAddress || data.pickup || 'Pickup' },
    dropoff: { lat: dropoffLat, lng: dropoffLng, address: data.dropoff || data.dropAddress || data.destination || 'Dropoff' },
    pickupTime: data.temp1 || data.pickupTime || data.customerbooktim || 'ASAP',
    passenger: {
      name: data.customerName || data.caller_name || 'Customer',
      phoneMasked: (data.customerPhone || data.caller_phone || '').replace(/\d(?=\d{4})/g,'*') || '+44*******'
    },
    notes, tags,
    pricing: { estimate: typeof fare==='number'? fare : parseFloat(fare)||10, currency: 'GBP' },
    distanceFromDriver: Math.round(dist),
    etaToPickup: Math.round(dist/500*60),
    bidStatus: 'none',
    source: 'mqtt'
  };
}

function handleMqttJob(data){
  const j = mqttJobFromPayload(data);
  if(!j) return;
  // Filter by job range setting
  var rangeMiles = parseFloat(localStorage.getItem('bcu_demo_job_range') || '10');
  var rangeMeters = rangeMiles * 1609.34;
  if(j.distanceFromDriver > rangeMeters){
    dbg('[JOB] Skipped job ' + j.jobId + ' — ' + (j.distanceFromDriver/1000).toFixed(1) + 'km exceeds range ' + rangeMiles + 'mi');
    return;
  }
  availableJobs.push(j);
  renderAvailableJobs();
  const m = L.circleMarker([j.pickup.lat,j.pickup.lng],{radius:5,fillColor:'#ef4444',color:'#fff',weight:1,fillOpacity:.8}).addTo(map).bindTooltip(j.pickup.address);
  jobMarkers['avail_'+j.jobId]=m;
  toast(`📍 New job: ${j.pickup.address}`,'info');
  announceNewJob(j);
}

function updateMqttStatus(status){
  const pill = document.getElementById('mqttPill');
  const text = document.getElementById('mqttText');
  pill.classList.remove('mqtt-pill');
  if(status==='connected'){ pill.classList.add('mqtt-pill'); text.textContent='MQTT Live'; mqttConnected=true; }
  else if(status==='error'){ pill.style.background='rgba(239,68,68,.15)'; pill.style.color='var(--red)'; text.textContent='MQTT Error'; }
  else { pill.style.background='rgba(245,158,11,.15)'; pill.style.color='var(--orange)'; text.textContent='MQTT Reconnecting…'; }
}

function connectMqtt(){
  dbg('🔌 Connecting to MQTT broker: ' + MQTT_BROKER);
  document.getElementById('mqttText').textContent = 'MQTT Connecting...';
  if(typeof mqtt === 'undefined' || !mqtt.connect){
    dbg('❌ mqtt library not loaded! typeof mqtt=' + typeof mqtt);
    document.getElementById('mqttText').textContent = 'MQTT lib missing';
    return;
  }
  dbg('✅ mqtt lib found, version: ' + (mqtt.version || 'unknown'));
  try {
    const clientId = `driver_${DRIVER_ID}_${Math.random().toString(36).substr(2, 8)}`;
    dbg('🔌 ClientId: ' + clientId);
    mqttClient = mqtt.connect(MQTT_BROKER, {
      clientId: clientId,
      reconnectPeriod: 2000,
      connectTimeout: 10000,
      keepalive: 60,
      clean: true,
      protocolVersion: 4  // MQTT 3.1.1 — required for public brokers
    });
    dbg('🔌 mqtt.connect() called, waiting for events...');

    mqttClient.on('connect', ()=>{
      dbg('✅ MQTT connected as: ' + DRIVER_ID);
      updateMqttStatus('connected');
      mqttClient.subscribe('pubs/requests/+');
      mqttClient.subscribe('taxi/bookings');
      mqttClient.subscribe('passengers/+/created');
      mqttClient.subscribe(`drivers/${DRIVER_ID}/bid-request`);
      mqttClient.subscribe(`drivers/${DRIVER_ID}/jobs`);
      mqttClient.subscribe(`jobs/+/result/${DRIVER_ID}`);
      mqttClient.subscribe('drivers/+/location');
      mqttClient.subscribe('drivers/+/status');
      mqttClient.subscribe('chat/group');
      mqttClient.subscribe('radio/broadcast');
      mqttClient.subscribe('radio/channel');
      mqttClient.subscribe('radio/driver/' + DRIVER_ID);
      dbg('📡 Subscribed to all topics. Driver ID: ' + DRIVER_ID);
      publishDriverStatus();
    });

    mqttClient.on('message', (topic, message)=>{
      try {
        const data = JSON.parse(message.toString());
        console.log('📡 MQTT received:', topic, data);

        if(topic.startsWith('drivers/') && topic.endsWith('/status')){
          const dId = topic.split('/')[1];
          if(dId !== DRIVER_ID){
            otherDrivers[dId] = { lat: data.lat||52.4068, lng: data.lng||-1.5197, status: data.status||'offline', lastUpdate: Date.now() };
            driverStatus[dId] = data.status || 'offline';
          }
        }

        if(topic === 'chat/group'){
          const msg = { id: Date.now().toString(), sender: data.driver||'unknown', text: data.text||'', timestamp: data.ts||Date.now() };
          chatMessages.group.push(msg);
          if(chatMessages.group.length > 50) chatMessages.group.shift();
          localStorage.setItem(`chat_group_${DRIVER_ID}`, JSON.stringify(chatMessages.group));
        }

        // Radio audio messages (broadcast, channel, or private)
        if(topic === 'radio/broadcast' || topic === 'radio/channel' || topic === 'radio/driver/' + DRIVER_ID){
          if(data.driver !== DRIVER_ID && data.audio){
            // If broadcast has a targets list, only play if we're in it
            if(topic === 'radio/broadcast' && data.targets && Array.isArray(data.targets)){
              if(!data.targets.includes(DRIVER_ID)) return;
            }
            var source = topic === 'radio/channel' ? 'driver' : 'dispatch';
            handleRadioReceive(data, source);
          }
        }

        if(topic.startsWith('pubs/requests/') || topic.includes('/bid-request') || topic.includes('/jobs') || topic === 'taxi/bookings' || topic.startsWith('dispatch/jobs/offer/')){
          handleMqttJob(data);
        }

        if(topic.includes(`/result/${DRIVER_ID}`)){
          const jobId = data.jobId || data.job;
          const j = availableJobs.find(x=>x.jobId===jobId);
          if(data.result==='won' && j){
            j.bidStatus='won';
            availableJobs = availableJobs.filter(x=>x.jobId!==jobId);
            j.state='ASSIGNED';
            inboxJobs.push(j);
            document.getElementById('inboxCount').textContent=inboxJobs.length;
            toast('🎉 You won the job!','success');
            announceBidResult(true,j);
            renderAvailableJobs();
            switchTab('inbox');
            showInboxJob(j);
          } else if(data.result==='lost'){
            toast('❌ Another driver selected','error');
            if(j) announceBidResult(false,j);
          }
        }
      } catch(e){
        console.error('MQTT message error:', e);
        updateMqttStatus('error');
      }
    });

    mqttClient.on('error', (err)=>{
      dbg('❌ MQTT error: ' + (err.message||err));
      mqttRetryCount++;
      if(mqttRetryCount >= 3 && currentBrokerIdx < MQTT_BROKERS.length - 1){
        currentBrokerIdx++;
        MQTT_BROKER = MQTT_BROKERS[currentBrokerIdx];
        dbg('🔀 Switching to fallback broker: ' + MQTT_BROKER);
        mqttClient.end(true);
        mqttRetryCount = 0;
        setTimeout(connectMqtt, 500);
        return;
      }
      updateMqttStatus('error');
    });
    mqttClient.on('reconnect', ()=>{
      mqttRetryCount++;
      dbg('🔄 Reconnecting... (attempt ' + mqttRetryCount + ')');
      if(mqttRetryCount >= 3 && currentBrokerIdx < MQTT_BROKERS.length - 1){
        currentBrokerIdx++;
        MQTT_BROKER = MQTT_BROKERS[currentBrokerIdx];
        dbg('🔀 Switching to fallback broker: ' + MQTT_BROKER);
        mqttClient.end(true);
        mqttRetryCount = 0;
        setTimeout(connectMqtt, 500);
        return;
      }
      updateMqttStatus('reconnecting');
    });
    mqttClient.on('offline', ()=>{
      dbg('📴 Broker offline - will retry');
      updateMqttStatus('reconnecting');
    });
  } catch(e){
    dbg('❌ Connection failed: ' + e.message);
    updateMqttStatus('error');
  }
}

// Test raw WebSocket connectivity before MQTT
function testWsConnectivity(){
  dbg('🧪 Testing raw WebSocket to HiveMQ...');
  try {
    var ws = new WebSocket('wss://broker.hivemq.com:8884/mqtt', ['mqtt']);
    ws.onopen = function(){ dbg('🧪 ✅ WebSocket opened to HiveMQ!'); ws.close(); };
    ws.onerror = function(e){ dbg('🧪 ❌ WebSocket failed to HiveMQ: ' + JSON.stringify(e)); };
    ws.onclose = function(e){ dbg('🧪 WebSocket closed. Code: ' + e.code + ' Clean: ' + e.wasClean); };
    setTimeout(function(){ if(ws.readyState === 0){ dbg('🧪 ⏰ WebSocket timed out after 5s'); ws.close(); } }, 5000);
  } catch(e){ dbg('🧪 ❌ WebSocket constructor threw: ' + e.message); }
}

// Override bidOnJob to publish real MQTT bids
const _originalBid = bidOnJob;
bidOnJob = function(id){
  const j = availableJobs.find(x=>x.jobId===id);
  if(!j) return;
  j.bidStatus = 'pending';
  renderAvailableJobs();
  toast('⚡ Bid submitted — waiting…','info');

  if(mqttClient && mqttConnected){
    mqttClient.publish(`jobs/${id}/bids`, JSON.stringify({
      driverId: DRIVER_ID, jobId: id,
      lat: driverLat, lng: driverLng, timestamp: Date.now()
    }));
    mqttClient.publish(`jobs/${id}/response`, JSON.stringify({
      driver: DRIVER_ID, driverId: DRIVER_ID, jobId: id, accepted: true
    }));
  }

  setTimeout(()=>{
    if(!j || j.bidStatus!=='pending') return;
    if(j.source==='mqtt') return;
    if(Math.random()>.3){
      j.bidStatus='won';
      availableJobs=availableJobs.filter(x=>x.jobId!==id);
      j.state='ASSIGNED';
      inboxJobs.push(j);
      document.getElementById('inboxCount').textContent=inboxJobs.length;
      toast('🎉 You won the job!','success');
      announceBidResult(true,j);
      renderAvailableJobs();
      switchTab('inbox');
      showInboxJob(j);
    } else {
      j.bidStatus='none';
      availableJobs=availableJobs.filter(x=>x.jobId!==id);
      toast('❌ Another driver selected','error');
      announceBidResult(false,j);
      renderAvailableJobs();
    }
  },2000);
};

// Seed initial mock jobs for demo (only if no saved jobs)
function addMockJobs(){
  if (availableJobs.length > 0) {
    dbg('[BOOT] Restored ' + availableJobs.length + ' saved available jobs');
    // Re-add map markers for restored jobs
    availableJobs.forEach(function(j){
      var m = L.circleMarker([j.pickup.lat,j.pickup.lng],{radius:5,fillColor:'#ef4444',color:'#fff',weight:1,fillOpacity:.8}).addTo(map).bindTooltip(j.pickup.address);
      jobMarkers['avail_'+j.jobId] = m;
    });
    renderAvailableJobs();
    document.getElementById('inboxCount').textContent = inboxJobs.length;
    return;
  }
  for(let i=0;i<4;i++){
    const j=generateJob();
    availableJobs.push(j);
    const m=L.circleMarker([j.pickup.lat,j.pickup.lng],{radius:5,fillColor:'#ef4444',color:'#fff',weight:1,fillOpacity:.8}).addTo(map).bindTooltip(j.pickup.address);
    jobMarkers['avail_'+j.jobId]=m;
  }
  renderAvailableJobs();
}

// Test WebSocket connectivity then connect MQTT
dbg('🚀 Starting MQTT connection sequence...');
testWsConnectivity();
connectMqtt();

// Seed mock jobs
addMockJobs();
setInterval(()=>{
  if(availableJobs.length<3 && !mqttConnected){
    const j=generateJob();
    availableJobs.push(j);
    renderAvailableJobs();
    const m=L.circleMarker([j.pickup.lat,j.pickup.lng],{radius:5,fillColor:'#ef4444',color:'#fff',weight:1,fillOpacity:.8}).addTo(map).bindTooltip(j.pickup.address);
    jobMarkers['avail_'+j.jobId]=m;
    toast(`📍 New job: ${j.pickup.address}`,'info');
    announceNewJob(j);
  }
},12000);

// ── GPS INITIALIZATION ──
let gpsActive = false;
let gpsWatchId = null;
let driverHeading = 0;

function initGPS() {
  var mapInfo = document.getElementById('mapInfo');
  if (!navigator.geolocation) {
    mapInfo.innerHTML = '<span class="gps-source gps-poor"></span>No GPS available';
    dbg('[GPS] Geolocation API not available — using simulated drift');
    return;
  }

  mapInfo.innerHTML = '<span class="gps-source gps-poor"></span>Searching for GPS...';
  dbg('[GPS] Requesting device location...');

  gpsWatchId = navigator.geolocation.watchPosition(
    function(position) {
      gpsActive = true;
      driverLat = position.coords.latitude;
      driverLng = position.coords.longitude;
      var accuracy = position.coords.accuracy || 50;
      driverHeading = position.coords.heading || 0;

      // Update marker position
      driverMarker.setLatLng([driverLat, driverLng]);

      // Pan map to follow driver (only if user isn't dragging)
      if (!map.dragging || !map.dragging.moved || !map.dragging.moved()) {
        map.panTo([driverLat, driverLng], { animate: false });
      }

      // Determine GPS quality indicator
      var cls = 'gps-gps';
      if (accuracy > 50) cls = 'gps-poor';
      else if (accuracy > 20) cls = 'gps-network';

      mapInfo.innerHTML = '<span class="gps-source ' + cls + '"></span>' +
        'GPS: ' + driverLat.toFixed(5) + ', ' + driverLng.toFixed(5) +
        ' <span style="margin-left:8px;font-size:12px;opacity:0.8">(±' + Math.round(accuracy) + 'm)</span>';

      dbg('[GPS] ' + driverLat.toFixed(5) + ',' + driverLng.toFixed(5) + ' acc=' + Math.round(accuracy) + 'm');
    },
    function(error) {
      var errorMsg = 'GPS error: ';
      switch(error.code) {
        case 1: errorMsg += 'Location access denied.'; break;
        case 2: errorMsg += 'Location unavailable.'; break;
        case 3: errorMsg += 'Location request timed out.'; break;
        default: errorMsg += error.message;
      }
      mapInfo.innerHTML = '<span class="gps-source gps-poor"></span>' + errorMsg;
      dbg('[GPS] ' + errorMsg + ' — using simulated drift');
    },
    { enableHighAccuracy: true, maximumAge: 5000, timeout: 15000 }
  );
}

initGPS();

// Fallback simulated drift + periodic updates
setInterval(function(){
  if (!gpsActive) {
    driverLat += (Math.random()-.5)*0.001;
    driverLng += (Math.random()-.5)*0.001;
    driverMarker.setLatLng([driverLat, driverLng]);
  }
  availableJobs.forEach(function(j){ j.distanceFromDriver=haversine(driverLat,driverLng,j.pickup.lat,j.pickup.lng); j.etaToPickup=Math.round(j.distanceFromDriver/500*60); });
  if(document.getElementById('tabAvail').classList.contains('active')&&!activeInboxJob) renderAvailableJobs();
  if(mqttClient && mqttConnected){
    mqttClient.publish('drivers/'+DRIVER_ID+'/location', JSON.stringify({
      driver: DRIVER_ID, name: DRIVER_NAME, registration: VEHICLE_REG, vehicle: VEHICLE_TYPE,
      lat:driverLat, lng:driverLng, status:driverPresence, ts:Date.now(), heading:driverHeading
    }));
  }
},5000);

// ── SCREEN WAKE LOCK ──
let wakeLock = null;

async function requestWakeLock() {
  try {
    if ('wakeLock' in navigator) {
      wakeLock = await navigator.wakeLock.request('screen');
      dbg('[WAKE] Screen wake lock acquired');
      wakeLock.addEventListener('release', function() {
        dbg('[WAKE] Screen wake lock released');
      });
    }
  } catch(e) {
    dbg('[WAKE] Wake lock failed: ' + e.message);
  }
}

async function releaseWakeLock() {
  if (wakeLock) {
    await wakeLock.release();
    wakeLock = null;
  }
}

// Re-acquire wake lock when page becomes visible again
document.addEventListener('visibilitychange', function() {
  if (document.visibilityState === 'visible' && document.fullscreenElement) {
    requestWakeLock();
  }
});

// ── FULLSCREEN TOGGLE ──
function toggleFullscreen(){
  var pill=document.getElementById('fsPill');
  if(document.fullscreenElement){
    document.exitFullscreen();
    releaseWakeLock();
    if(pill) pill.innerHTML='⛶ Fullscreen';
  } else {
    document.documentElement.requestFullscreen().then(function(){
      requestWakeLock();
    });
    if(pill) pill.innerHTML='⊡ Exit FS';
  }
}
document.addEventListener('fullscreenchange',function(){
  var pill=document.getElementById('fsPill');
  if(document.fullscreenElement){
    pill.innerHTML='⊡ Exit FS';
    requestWakeLock();
  } else {
    pill.innerHTML='⛶ Fullscreen';
    releaseWakeLock();
  }
});

// ══════════════════════════════════════════════════════
// ── WALKIE-TALKIE RADIO (Push-to-Talk over MQTT) ──
// ══════════════════════════════════════════════════════

var radioOpen = false;
var pttActive = false;
var radioVolume = 0.8;
var radioMediaStream = null;
var radioMediaRecorder = null;
var radioAudioCtx = null;
var radioLogMessages = [];

function toggleRadioPanel(){
  radioOpen = !radioOpen;
  document.getElementById('radioWidget').style.display = radioOpen ? 'block' : 'none';
  if(radioOpen) initRadioAudio();
}

function setRadioVolume(val){
  radioVolume = parseInt(val) / 100;
}

var radioGainNode = null;

async function initRadioAudio(){
  if(radioAudioCtx) return;
  try {
    radioAudioCtx = new (window.AudioContext || window.webkitAudioContext)();
    radioGainNode = radioAudioCtx.createGain();
    radioGainNode.gain.value = radioVolume;
    radioGainNode.connect(radioAudioCtx.destination);
    dbg('[RADIO] AudioContext initialized, sampleRate=' + radioAudioCtx.sampleRate);
  } catch(e){
    dbg('[RADIO] AudioContext failed: ' + e.message);
  }
}

async function pttStart(evt){
  if(evt) evt.preventDefault();
  if(pttActive) return;
  if(!mqttClient || !mqttConnected){
    toast('📻 Radio needs MQTT connection','error');
    return;
  }
  pttActive = true;
  document.getElementById('pttBtn').classList.add('active');
  document.getElementById('radioStatus').textContent = '🔴 TRANSMITTING...';
  document.getElementById('radioStatus').className = 'radio-status live';

  try {
    if(!radioMediaStream){
      radioMediaStream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true, sampleRate: 48000 }, video: false });
      dbg('[RADIO] Microphone access granted');
    }

    // Use MediaRecorder to capture audio in small chunks
    var mimeType = 'audio/webm;codecs=opus';
    if(!MediaRecorder.isTypeSupported(mimeType)){
      mimeType = 'audio/webm';
      if(!MediaRecorder.isTypeSupported(mimeType)) mimeType = 'audio/ogg;codecs=opus';
    }

    radioMediaRecorder = new MediaRecorder(radioMediaStream, { mimeType: mimeType, audioBitsPerSecond: 32000 });

    radioMediaRecorder.ondataavailable = function(e){
      if(e.data.size > 0 && pttActive){
        var reader = new FileReader();
        reader.onloadend = function(){
          var base64 = reader.result.split(',')[1];
          if(base64 && mqttClient && mqttConnected){
            mqttClient.publish('radio/channel', JSON.stringify({
              driver: DRIVER_ID,
              name: DRIVER_NAME,
              audio: base64,
              mime: mimeType,
              ts: Date.now(),
              seq: Date.now()
            }));
          }
        };
        reader.readAsDataURL(e.data);
      }
    };

    radioMediaRecorder.start(500); // 500ms chunks
    dbg('[RADIO] Recording started, chunk interval=500ms');
    addRadioLog('outgoing', DRIVER_NAME, 'Transmitting...');
  } catch(e){
    dbg('[RADIO] Mic error: ' + e.message);
    toast('📻 Microphone access denied','error');
    pttStop();
  }
}

function pttStop(evt){
  if(evt) evt.preventDefault();
  if(!pttActive) return;
  pttActive = false;
  document.getElementById('pttBtn').classList.remove('active');
  document.getElementById('radioStatus').textContent = 'Ready — Hold to talk';
  document.getElementById('radioStatus').className = 'radio-status';

  if(radioMediaRecorder && radioMediaRecorder.state !== 'inactive'){
    radioMediaRecorder.stop();
    dbg('[RADIO] Recording stopped');
  }
  radioMediaRecorder = null;
}

// Receive and play incoming radio audio
var radioPlayQueue = [];
var radioPlaying = false;

function handleRadioReceive(data, source){
  var label = source === 'dispatch' ? '📡 ' + (data.name || 'Dispatch') : '🚕 ' + (data.name || data.driver);
  addRadioLog('incoming', label, 'Audio received');

  // Flash the toggle button if panel is closed
  if(!radioOpen){
    var btn = document.getElementById('radioToggleBtn');
    btn.classList.add('has-activity');
    setTimeout(function(){ btn.classList.remove('has-activity'); }, 3000);
  }

  // Update status
  document.getElementById('radioStatus').textContent = '🟢 ' + label + ' speaking...';
  document.getElementById('radioStatus').className = 'radio-status receiving';
  clearTimeout(window._radioStatusTimer);
  window._radioStatusTimer = setTimeout(function(){
    if(!pttActive){
      document.getElementById('radioStatus').textContent = 'Ready — Hold to talk';
      document.getElementById('radioStatus').className = 'radio-status';
    }
  }, 2000);

  // Decode and play via Web Audio API for better quality
  try {
    initRadioAudio();
    var binaryStr = atob(data.audio);
    var bytes = new Uint8Array(binaryStr.length);
    for(var i=0; i<binaryStr.length; i++) bytes[i] = binaryStr.charCodeAt(i);
    var blob = new Blob([bytes], { type: data.mime || 'audio/webm;codecs=opus' });
    blob.arrayBuffer().then(function(arrayBuf){
      if(radioAudioCtx.state === 'suspended') radioAudioCtx.resume();
      radioAudioCtx.decodeAudioData(arrayBuf, function(audioBuf){
        var source = radioAudioCtx.createBufferSource();
        source.buffer = audioBuf;
        if(radioGainNode){
          radioGainNode.gain.value = radioVolume;
          source.connect(radioGainNode);
        } else {
          source.connect(radioAudioCtx.destination);
        }
        source.start();
      }, function(err){
        dbg('[RADIO] decodeAudioData error: ' + err);
      });
    });
  } catch(e){
    dbg('[RADIO] Decode error: ' + e.message);
  }
}

function addRadioLog(type, name, text){
  var now = new Date();
  var timeStr = now.toLocaleTimeString([], { hour:'2-digit', minute:'2-digit', second:'2-digit' });
  radioLogMessages.push({ type: type, name: name, text: text, time: timeStr });
  if(radioLogMessages.length > 20) radioLogMessages.shift();
  renderRadioLog();
}

function renderRadioLog(){
  var el = document.getElementById('radioLog');
  if(!el) return;
  el.innerHTML = radioLogMessages.map(function(m){
    return '<div class="radio-msg ' + m.type + '"><span class="rm-name">' + m.name + '</span> ' + m.text + '<span class="rm-time">' + m.time + '</span></div>';
  }).join('');
  el.scrollTop = el.scrollHeight;
}

dbg('[RADIO] Walkie-talkie module loaded');
