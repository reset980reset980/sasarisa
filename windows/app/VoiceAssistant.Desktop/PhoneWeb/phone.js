'use strict';
const $ = id => document.getElementById(id);
let token = sessionStorage.getItem('jarvis-phone-token') || '';
let pending = null, polling = false;
try { pending = JSON.parse(sessionStorage.getItem('jarvis-phone-pending') || 'null'); } catch { sessionStorage.removeItem('jarvis-phone-pending'); }
function notice(text) { $('notice').textContent = text; }
function show(connected) { $('pairPanel').hidden = connected; $('chatPanel').hidden = !connected; }
function remember() { if (pending) sessionStorage.setItem('jarvis-phone-pending', JSON.stringify(pending)); else sessionStorage.removeItem('jarvis-phone-pending'); $('send').disabled = !!pending; }
function message(text, role) { const item = document.createElement('article'); item.className = role; item.textContent = text; $('messages').append(item); while ($('messages').children.length > 60) $('messages').firstElementChild.remove(); $('messages').scrollTop = $('messages').scrollHeight; }
async function api(path, body) {
  const abort = new AbortController(), timer = setTimeout(() => abort.abort(), 10000);
  try {
    const response = await fetch(path, {method: body === undefined ? 'GET' : 'POST', headers: {'X-Sasarisa-Client':'1', ...(body === undefined ? {} : {'Content-Type':'application/json'}), ...(token ? {'Authorization':'Bearer '+token} : {})}, body: body === undefined ? undefined : JSON.stringify(body), cache:'no-store', credentials:'omit', signal:abort.signal});
    const data = await response.json();
    if (!response.ok) { if (response.status === 401 && path !== '/api/pair') { token = ''; sessionStorage.removeItem('jarvis-phone-token'); show(false); } throw new Error(data.error || '연결 상태를 확인하세요.'); }
    return data;
  } catch (error) { if (error instanceof TypeError || error.name === 'AbortError') throw new Error('PC 연결이 끊겼어요. 같은 와이파이와 PC 연결 상태를 확인한 뒤 다시 확인을 누르세요.'); throw error; }
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
  try { const data = await api('/api/pair', {code:$('code').value.trim()}); token = data.token; sessionStorage.setItem('jarvis-phone-token',token); $('code').value = ''; pending = null; remember(); show(true); $('retry').hidden = true; notice('PC에 연결됐어요.'); }
  catch (error) { notice(error.message); } finally { $('pairButton').disabled = false; }
});
$('commandForm').addEventListener('submit', event => {
  event.preventDefault(); if (pending) return; const text = $('command').value.trim(); if (!text) return;
  pending = {id:newId(),text}; remember(); message(text,'question'); $('command').value = ''; deliver();
});
document.querySelectorAll('[data-command]').forEach(button => button.addEventListener('click', () => { $('command').value = button.dataset.command; $('command').focus(); }));
$('retry').addEventListener('click', deliver);
$('stop').addEventListener('click', async () => { try { notice((await api('/api/stop',{})).message); } catch (error) { notice(error.message); } });
$('logout').addEventListener('click', async () => {
  try { await api('/api/logout',{}); token = ''; pending = null; sessionStorage.removeItem('jarvis-phone-token'); remember(); show(false); notice('연결을 끊었어요.'); }
  catch (error) { notice(error.message); }
});
async function resume() { show(!!token); remember(); if (!token) return; try { await api('/api/status'); if (pending) { message(pending.text,'question'); await deliver(); } else notice('PC에 연결됐어요.'); } catch (error) { notice(error.message); } }
resume();
