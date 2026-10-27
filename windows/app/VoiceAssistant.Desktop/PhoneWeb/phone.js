'use strict';
const $ = id => document.getElementById(id);
const storage = {get(key) { try { return sessionStorage.getItem(key); } catch { return null; } }, set(key,value) { try { if(value===null)sessionStorage.removeItem(key);else sessionStorage.setItem(key,value); } catch { } }};
let token = storage.get('jarvis-phone-token') || '';
let pending = null, polling = false;
try { pending = JSON.parse(storage.get('jarvis-phone-pending') || 'null'); if(pending && (!/^[a-f0-9]{32}$/.test(pending.id) || typeof pending.text!=='string'))pending=null; } catch { storage.set('jarvis-phone-pending',null); }
let voiceAvailable=false, recognition=null, voiceTimer=null;
const Speech = window.SpeechRecognition || window.webkitSpeechRecognition;
function notice(text) { $('notice').textContent = text; }
function connectionInfo(data) {
  const client=data?.client, kind=client?.kind;
  const version=typeof client?.version==='string' && /^\d{1,4}\.\d{1,4}\.\d{1,4}$/.test(client.version) ? ' · '+client.version : '';
  $('connectionName').textContent=kind==='preview'?'● 시험용 PC 연결'+version:kind==='standard'?'● 자비스 PC 연결'+version:'● PC 연결 · 종류 확인 필요';
  $('connectionDetail').textContent=kind==='preview'?'이 연결은 화면 시험용입니다. 시계 명령만 실행하고 PC 음성은 꺼져 있습니다. 연결을 끊고 일반 자비스 앱의 휴대폰 연결 · 무료에서 새 코드를 받아 연결하세요.':kind==='standard'?'시계·타이머·앱 실행과 AI 질문을 보낼 수 있습니다. PC에서 확인이 필요한 동작은 PC에서 요청하세요. 답변 음성은 PC의 답변 읽기·출력 장치 설정을 따릅니다.':'PC 앱이 연결 종류를 알려주지 않았습니다. 시계만 실행되거나 소리가 없으면 일반 자비스 앱에서 새 연결 코드를 받아 다시 연결하세요.';
  $('connectionDetail').className=kind==='standard'?'hint':'connection-warning';
}
function show(connected) { if(!connected)cancelVoice(); $('pairPanel').hidden = connected; $('chatPanel').hidden = !connected; }
function remember() { storage.set('jarvis-phone-pending',pending ? JSON.stringify(pending) : null); $('send').disabled=!!pending || !!recognition; $('mic').disabled=!!pending || !voiceAvailable; }
function message(text, role) { const item = document.createElement('article'); item.className = role; item.textContent = text; $('messages').append(item); while ($('messages').children.length > 60) $('messages').firstElementChild.remove(); $('messages').scrollTop = $('messages').scrollHeight; }
async function api(path, body) {
  const abort = new AbortController(), timer = setTimeout(() => abort.abort(), 10000);
  try {
    const response = await fetch(path, {method: body === undefined ? 'GET' : 'POST', headers: {'X-Sasarisa-Client':'1', ...(body === undefined ? {} : {'Content-Type':'application/json'}), ...(token ? {'Authorization':'Bearer '+token} : {})}, body: body === undefined ? undefined : JSON.stringify(body), cache:'no-store', credentials:'omit', signal:abort.signal});
    const data = await response.json();
    if (!response.ok) { if (response.status === 401 && path !== '/api/pair') { token = ''; storage.set('jarvis-phone-token',null); show(false); } throw new Error(data.error || '연결 상태를 확인하세요.'); }
    return data;
  } catch (error) { if (error instanceof TypeError || error.name === 'AbortError') throw new Error('PC 연결이 끊겼어요. 인터넷과 PC 앱의 연결 상태를 확인한 뒤 다시 확인을 누르세요.'); throw error; }
  finally { clearTimeout(timer); }
}
function newId() { const b = new Uint8Array(16); crypto.getRandomValues(b); return Array.from(b, n => n.toString(16).padStart(2,'0')).join(''); }
async function deliver() {
  if (!pending || polling || !token) return;
  polling = true; const command = pending, session = token; $('retry').hidden = true; remember(); notice('PC에 전달하고 있어요…');
  try {
    let result = await api('/api/command', command);
    while (result.pending) { if (session !== token) return; notice('PC에서 처리 중이에요…'); await new Promise(r => setTimeout(r, 900)); if (session !== token) return; result = await api('/api/result?id='+command.id); }
    if (session !== token) return;
    message(result.result, 'answer'); pending = null; remember(); notice('완료');
  } catch (error) { notice(error.message); $('retry').hidden = !token; }
  finally { polling = false; }
}
$('pairForm').addEventListener('submit', async event => {
  event.preventDefault(); $('pairButton').disabled = true;
  try { const data = await api('/api/pair', {code:$('code').value.trim()}); token = data.token; storage.set('jarvis-phone-token',token); $('code').value = ''; pending = null; remember(); show(true); connectionInfo(data); $('retry').hidden = true; notice('PC에 연결됐어요.'); }
  catch (error) { notice(error.message); } finally { $('pairButton').disabled = false; }
});
$('commandForm').addEventListener('submit', event => {
  event.preventDefault(); if (pending || recognition) return; const text = $('command').value.trim(); if (!text) return;
  if(text.length>300) { notice('명령을 300자 이내로 줄인 뒤 보내세요.'); return; }
  pending = {id:newId(),text}; remember(); message(text,'question'); $('command').value = ''; deliver();
});
document.querySelectorAll('[data-command]').forEach(button => button.addEventListener('click', () => { $('command').value = button.dataset.command; $('command').focus(); }));
$('retry').addEventListener('click', deliver);
$('stop').addEventListener('click', async () => { cancelVoice(); try { notice((await api('/api/stop',{})).message); } catch (error) { notice(error.message); } });
$('logout').addEventListener('click', async () => {
  cancelVoice();
  try { await api('/api/logout',{}); token = ''; pending = null; storage.set('jarvis-phone-token',null); remember(); show(false); notice('연결을 끊었어요.'); }
  catch (error) { notice(error.message); }
});
function finishVoice() { clearTimeout(voiceTimer); voiceTimer=null; recognition=null; $('mic').textContent='🎤 음성 입력'; $('mic').setAttribute('aria-pressed','false'); $('command').readOnly=false; remember(); }
function cancelVoice() { const current=recognition; finishVoice(); if(current)current.abort(); }
$('mic').addEventListener('click',()=>{
  if(recognition) { recognition.stop(); return; }
  if(!voiceAvailable || pending || !token) return;
  const current=new Speech(), before=$('command').value.trim(); let heard=false;
  recognition=current; current.lang='ko-KR'; current.continuous=false; current.interimResults=true; current.maxAlternatives=1;
  $('mic').textContent='■ 말하기 마치기'; $('mic').setAttribute('aria-pressed','true'); $('command').readOnly=true; remember();
  current.onstart=()=>{ if(recognition===current)notice('듣고 있어요. 최대 30초 동안 말한 뒤 문장을 확인하세요.'); };
  current.onresult=event=>{ if(recognition!==current)return; const words=Array.from(event.results,result=>result[0].transcript).join(' ').trim(); if(words){heard=true;$('command').value=(before ? before+' ' : '')+words;} };
  current.onerror=event=>{ if(recognition!==current)return; notice(({ 'not-allowed':'마이크 사용이 허용되지 않았어요. 브라우저의 사이트 설정에서 직접 허용해 주세요.', 'service-not-allowed':'음성 서비스를 사용할 수 없어요. 갤럭시는 Chrome, 아이폰은 Safari에서 시도하세요.', 'audio-capture':'휴대폰 마이크를 사용할 수 없어요. 다른 녹음·통화 앱을 확인하세요.', 'no-speech':'목소리를 듣지 못했어요. 음성 입력을 다시 눌러 주세요.', 'network':'음성 인식 서비스에 연결하지 못했어요. 인터넷을 확인하세요.' })[event.error] || '음성 입력이 중지됐어요. 다시 시도하거나 키보드로 입력하세요.'); finishVoice(); };
  current.onend=()=>{ if(recognition!==current)return; finishVoice(); notice(heard ? ($('command').value.length>300 ? '인식한 내용이 길어요. 300자 이내로 줄인 뒤 보내세요.' : '인식한 문장을 확인한 뒤 보내기를 누르세요.') : '목소리를 듣지 못했어요. 다시 시도하세요.'); };
  try { current.start(); voiceTimer=setTimeout(()=>{if(recognition===current)current.stop();},30000); } catch { finishVoice(); notice('음성 입력을 시작하지 못했어요. 브라우저와 마이크 권한을 확인하세요.'); }
});
document.addEventListener('visibilitychange',()=>{if(document.hidden)cancelVoice();});
window.addEventListener('pagehide',cancelVoice);
async function resume() {
  show(!!token); remember();
  try {
    const info=await api('/api/info'), internet=info.mode==='internet';
    $('modeTag').textContent=internet?'인터넷 연결':'로컬 연결';
    $('intro').textContent=internet?'LTE·5G·다른 와이파이에서도 내 PC에 연결합니다.':'같은 와이파이에서 PC의 자비스와 연결합니다.';
    $('code').inputMode=internet?'text':'numeric'; $('code').maxLength=internet?14:8; $('code').pattern=internet?'[0-9A-Za-z -]{12,14}':'[0-9]{8}'; $('code').placeholder=internet?'예: ABCD-2345-EFGH':'8자리 숫자';
    $('pairHelp').textContent=internet?'PC에서 휴대폰 연결 · 무료 → 인터넷 → 연결 켜기를 누르고 12자리 코드를 입력하세요.':'PC의 휴대폰 연결 · 무료 화면에 표시된 8자리 코드를 입력하세요.';
    $('connectionHelp').textContent=internet?'명령과 답변은 HTTPS 중계 서버를 거칩니다. 회사망에서는 이 사이트 접속이 허용되어야 합니다.':'HTTP 연결입니다. 신뢰하는 집·사무실 와이파이에서만 사용하세요.';
    voiceAvailable=!!(info.voice && window.isSecureContext && Speech); remember();
    $('voiceHint').textContent=voiceAvailable?'음성 입력 후 문장을 확인하고 보내세요. 음성은 브라우저 제공업체(Google·Apple 등)의 인식 서비스에서 처리될 수 있습니다.':internet?'이 브라우저에서는 음성 버튼을 사용할 수 없어요. 갤럭시는 Chrome, 아이폰은 Safari를 사용하거나 키보드 마이크로 입력하세요.':'화면의 음성 버튼은 인터넷 연결 모드에서 사용할 수 있어요. 여기서는 키보드의 마이크로 입력하세요.';
    if (!token) return; const status=await api('/api/status'); connectionInfo(status);
    if (pending) { message(pending.text,'question'); await deliver(); } else notice(status.connected?'PC에 연결됐어요.':'PC가 응답하지 않고 있어요. PC 앱의 연결 상태를 확인하세요.');
  } catch (error) { notice(error.message); }
}
resume();
