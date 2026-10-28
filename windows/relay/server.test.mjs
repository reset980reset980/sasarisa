import test from 'node:test';
import assert from 'node:assert/strict';
import {randomBytes,createHash} from 'node:crypto';
import http from 'node:http';
import {createRelay} from './server.mjs';

const id=()=>randomBytes(16).toString('hex');
async function fixture(t, client) {
  let now=Date.now(); const origin='https://relay.example';
  const server=await createRelay({origin,clock:()=>now,pollMs:25});
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  t.after(()=>new Promise(resolve=>{server.close(resolve);server.closeAllConnections();}));
  const base='http://127.0.0.1:'+server.address().port;
  async function request(route,body,token='',extra={}) {
    return new Promise((resolve,reject)=>{
      const request=http.request(base+route,{method:body===undefined?'GET':'POST',headers:{Host:'relay.example',Origin:origin,'X-Sasarisa-Client':'1',...(token?{Authorization:'Bearer '+token}:{}),...(body===undefined?{}:{'Content-Type':'application/json'}),...extra}},response=>{
        let text='';response.setEncoding('utf8');response.on('data',part=>text+=part);response.on('end',()=>{try{resolve({status:response.statusCode,body:JSON.parse(text),headers:new Headers(response.headers)});}catch(error){reject(error);}});
      }); request.on('error',reject);request.end(body===undefined?undefined:JSON.stringify(body));
    });
  }
  const desktop=(await request('/api/desktop/open',client ? {client} : {})).body;
  const phone=(await request('/api/pair',{code:desktop.code})).body;
  return {request,desktop,phone,advance(ms){now+=ms;},base};
}

test('paired phone sees standard or preview profile from its own PC, including reconnect status',async t=>{
  for(const kind of ['standard','preview']) {
    const client={kind,version:'0.2.28'}, f=await fixture(t,client);
    assert.deepEqual(f.phone.client,client);
    assert.deepEqual((await f.request('/api/status',undefined,f.phone.token)).body.client,client);
    assert.equal((await f.request('/api/status')).status,401);
  }
});

test('desktop receives stable per-phone identity without raw credentials or client-supplied identity',async t=>{
  const f=await fixture(t), other=(await f.request('/api/pair',{code:f.desktop.code})).body;
  async function send(token) {
    const key=id();await f.request('/api/command',{id:key,text:'Aside 시험',peer:'forged'},token);
    const command=(await f.request('/api/desktop/poll',undefined,f.desktop.token)).body.command;
    await f.request('/api/desktop/done',{id:key,result:'ok'},f.desktop.token);return command;
  }
  const first=await send(f.phone.token), again=await send(f.phone.token), second=await send(other.token);
  assert.match(first.peer,/^[a-f0-9]{64}$/);assert.equal(first.peer,again.peer);assert.notEqual(first.peer,second.peer);
  assert.notEqual(first.peer,f.phone.token);assert.notEqual(first.peer,f.desktop.token);assert.notEqual(first.peer,'forged');
});

test('legacy PC is explicitly unknown and malformed profile text is refused',async t=>{
  const f=await fixture(t);assert.deepEqual(f.phone.client,{kind:'unknown',version:''});
  for(const client of [{kind:'standard',version:'<script>'},{kind:'admin',version:'0.2.28'},null])
    assert.equal((await f.request('/api/desktop/open',{client})).status,400);
});
test('HTTPS page config and microphone permission are explicit; no permissive CORS',async t=>{
  const f=await fixture(t),r=await f.request('/api/info');
  assert.deepEqual(r.body,{mode:'internet',voice:true}); assert.equal(r.headers.get('cache-control'),'no-store');
  assert.match(r.headers.get('permissions-policy'),/microphone=\(self\)/); assert.equal(r.headers.get('access-control-allow-origin'),null);
});
test('unknown token, foreign origin and rebinding host cannot access desktop or phone',async t=>{
  const f=await fixture(t);
  assert.equal((await f.request('/api/desktop/poll',undefined,'bad')).status,401);
  assert.equal((await f.request('/api/command',{id:id(),text:'hello'})).status,401);
  assert.equal((await f.request('/api/status',undefined,f.phone.token,{Origin:'https://evil.example'})).status,403);
  assert.equal((await f.request('/api/status',undefined,f.phone.token,{Host:'evil.example'})).status,403);
  assert.equal((await f.request('/api/pair',{code:f.desktop.code},'',{Origin:''})).status,403);
});
test('command crosses both clients, scoped result is returned once, replay does not enqueue',async t=>{
  const f=await fixture(t),key=id();
  assert.equal((await f.request('/api/command',{id:key,text:'지금 몇 시야'},f.phone.token)).body.pending,true);
  const delivery=await f.request('/api/desktop/poll',undefined,f.desktop.token); assert.deepEqual(delivery.body.command,{id:key,text:'지금 몇 시야',peer:createHash('sha256').update(f.phone.token).digest('hex')});
  await f.request('/api/desktop/done',{id:key,result:'오후 3시입니다.'},f.desktop.token);
  const duplicate=await f.request('/api/command',{id:key,text:'changed'},f.phone.token); assert.deepEqual(duplicate.body,{pending:false,result:'오후 3시입니다.'});
  assert.equal((await f.request('/api/desktop/poll',undefined,f.desktop.token)).body.command,null);
});
test('another phone cannot read, reuse, or cancel the first phone request',async t=>{
  const f=await fixture(t),other=(await f.request('/api/pair',{code:f.desktop.code})).body,key=id();
  await f.request('/api/command',{id:key,text:'hello'},f.phone.token);
  await f.request('/api/stop',{},other.token);
  assert.equal((await f.request('/api/result?id='+key,undefined,other.token)).status,404);
  assert.equal((await f.request('/api/command',{id:key,text:'changed'},other.token)).status,404);
  assert.deepEqual((await f.request('/api/desktop/poll',undefined,f.desktop.token)).body.cancelled,[]);
  await f.request('/api/stop',{},f.phone.token);
  assert.deepEqual((await f.request('/api/desktop/poll',undefined,f.desktop.token)).body.cancelled,[key]);
});
test('busy commands are completed without queueing; cancelled work is not redelivered',async t=>{
  const f=await fixture(t),first=id(),second=id();
  await f.request('/api/command',{id:first,text:'hello'},f.phone.token);
  assert.equal((await f.request('/api/command',{id:second,text:'another'},f.phone.token)).body.pending,false);
  await f.request('/api/stop',{},f.phone.token);
  const poll=await f.request('/api/desktop/poll',undefined,f.desktop.token); assert.equal(poll.body.command,null); assert.deepEqual(poll.body.cancelled,[first]);
  await f.request('/api/desktop/done',{id:first,result:'cancelled'},f.desktop.token);
  assert.equal((await f.request('/api/result?id='+first,undefined,f.phone.token)).body.pending,false);
});
test('logout cancels only its own work and immediately revokes its token',async t=>{
  const f=await fixture(t),key=id(); await f.request('/api/command',{id:key,text:'hello'},f.phone.token);
  await f.request('/api/logout',{},f.phone.token);
  assert.equal((await f.request('/api/status',undefined,f.phone.token)).status,401);
  assert.deepEqual((await f.request('/api/desktop/poll',undefined,f.desktop.token)).body.cancelled,[key]);
});
test('closing PC revokes every phone and the pairing code',async t=>{
  const f=await fixture(t); await f.request('/api/desktop/close',{},f.desktop.token);
  assert.equal((await f.request('/api/status',undefined,f.phone.token)).status,401);
  assert.equal((await f.request('/api/pair',{code:f.desktop.code})).status,401);
});
test('invalid ids, controls, long text and large body never reach PC',async t=>{
  const f=await fixture(t);
  for(const text of ['', 'a\0b', '가'.repeat(301)]) assert.equal((await f.request('/api/command',{id:id(),text},f.phone.token)).status,400);
  assert.equal((await f.request('/api/command',{id:'bad',text:'hello'},f.phone.token)).status,400);
  assert.equal((await f.request('/api/command',{id:id(),text:'a'.repeat(5000)},f.phone.token)).status,413);
  assert.equal((await f.request('/api/desktop/poll',undefined,f.desktop.token)).body.command,null);
});
test('pair attempts and room creation are bounded',async t=>{
  const f=await fixture(t); let result;
  for(let i=0;i<11;i++)result=await f.request('/api/pair',{code:'WRONG'}); assert.equal(result.status,429);
  for(let i=0;i<6;i++)result=await f.request('/api/desktop/open',{}); assert.equal(result.status,429);
});
test('stale PC rejects new work and idle session is fully revoked',async t=>{
  const f=await fixture(t);f.advance(46000);
  assert.equal((await f.request('/api/status',undefined,f.phone.token)).body.connected,false);
  assert.equal((await f.request('/api/command',{id:id(),text:'hello'},f.phone.token)).status,409);
  f.advance(180000); assert.equal((await f.request('/api/status',undefined,f.phone.token)).status,401);
});
test('active PC pairing code expires after ten minutes and cannot admit another phone',async t=>{
  const f=await fixture(t);
  for(let i=0;i<7;i++){f.advance(90000);await f.request('/api/desktop/poll',undefined,f.desktop.token);}
  assert.equal((await f.request('/api/pair',{code:f.desktop.code})).status,401);
  assert.equal((await f.request('/api/status',undefined,f.phone.token)).status,200);
});
test('unfinished delivery is retried with same ID, expired work is cancelled',async t=>{
  const f=await fixture(t),key=id(); await f.request('/api/command',{id:key,text:'hello'},f.phone.token);
  assert.equal((await f.request('/api/desktop/poll',undefined,f.desktop.token)).body.command.id,key);
  f.advance(11000); assert.equal((await f.request('/api/desktop/poll',undefined,f.desktop.token)).body.command.id,key);
  for(let i=0;i<4;i++){f.advance(80000);await f.request('/api/desktop/poll',undefined,f.desktop.token);}
  assert.deepEqual((await f.request('/api/desktop/poll',undefined,f.desktop.token)).body.cancelled,[key]);
});
