import http from 'node:http';
import { randomBytes, randomInt } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const alphabet = '23456789ABCDEFGHJKLMNPQRSTUVWXYZ';
const secret = () => randomBytes(32).toString('hex');
const idPattern = /^[a-f0-9]{32}$/;
const cleanText = value => typeof value === 'string' && value.trim().length > 0 && value.length <= 1200 && [...new Intl.Segmenter('ko', {granularity:'grapheme'}).segment(value)].length <= 300 && !/[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]/.test(value);

// Outbound HTTPS polling only. Credentials, pending commands and replies live in RAM.
// The reverse proxy must reach this listener over loopback and overwrite X-Forwarded-For.
export async function createRelay({origin, clock = Date.now, pollMs = 20000, webRoot = path.join(here, '../app/VoiceAssistant.Desktop/PhoneWeb')} = {}) {
  const site = new URL(origin);
  if (site.origin !== origin || (site.protocol !== 'https:' && site.hostname !== '127.0.0.1')) throw new Error('An exact HTTPS origin is required');
  const assets = new Map(await Promise.all([['/','index.html','text/html; charset=utf-8'],['/phone.js','phone.js','text/javascript; charset=utf-8'],['/phone.css','phone.css','text/css; charset=utf-8']].map(async ([route,file,type]) => [route,{body:await readFile(path.join(webRoot,file)),type}])));
  const rooms = new Map(), codes = new Map(), phones = new Map(), limits = new Map();
  function rate(key, max, period = 60000) {
    let entry = limits.get(key);
    if (!entry || entry.until <= clock()) { entry = {count:0,until:clock()+period}; limits.set(key,entry); }
    return ++entry.count <= max;
  }
  function wake(room) { const notify = room.wait; room.wait = null; notify?.(); }
  function cancel(room, phone) {
    for (const row of room.commands.values()) if (!row.done && (!phone || row.owner === phone)) row.cancelled = true;
    wake(room);
  }
  function close(room) {
    rooms.delete(room.token); codes.delete(room.code);
    for (const key of room.phones) phones.delete(key);
    room.closed = true; wake(room);
  }
  function sweep() {
    for (const room of rooms.values()) {
      if (room.expires <= clock() || room.seen + 180000 < clock()) { close(room); continue; }
      if (room.pairUntil <= clock()) codes.delete(room.code);
      for (const row of room.commands.values()) if (!row.done && row.created + 300000 < clock()) { row.cancelled = true; wake(room); }
    }
    for (const [key,value] of limits) if (value.until <= clock()) limits.delete(key);
  }
  const timer = setInterval(sweep, 10000); timer.unref();
  async function body(req) {
    if (!req.headers['content-type']?.startsWith('application/json')) throw Object.assign(new Error('JSON 요청이 필요해요.'),{status:415});
    const parts = []; let size = 0;
    for await (const part of req) { size += part.length; if(size<=4096)parts.push(part); }
    if(size>4096)throw Object.assign(new Error('요청이 너무 커요.'),{status:413});
    try { const parsed = JSON.parse(Buffer.concat(parts)); if (!parsed || Array.isArray(parsed) || typeof parsed !== 'object') throw 0; return parsed; }
    catch { throw Object.assign(new Error('요청 형식을 확인하세요.'),{status:400}); }
  }
  const server = http.createServer(async (req,res) => {
    const json = (status,data) => { if (!res.destroyed && !res.writableEnded) { res.writeHead(status,{'Content-Type':'application/json; charset=utf-8'}); res.end(JSON.stringify(data)); } };
    res.setHeader('Cache-Control','no-store'); res.setHeader('X-Content-Type-Options','nosniff'); res.setHeader('Referrer-Policy','no-referrer'); res.setHeader('X-Frame-Options','DENY');
    res.setHeader('Content-Security-Policy',"default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'");
    res.setHeader('Permissions-Policy','camera=(), microphone=(self), geolocation=()');
    if(site.protocol==='https:')res.setHeader('Strict-Transport-Security','max-age=31536000');
    try {
      sweep();
      if (req.headers.host !== site.host || (req.headers.origin && req.headers.origin !== origin)) return json(403,{error:'허용되지 않은 접속입니다.'});
      const url = new URL(req.url,origin), route = url.pathname;
      if ((req.method === 'GET' || req.method === 'HEAD') && assets.has(route)) { const asset = assets.get(route); res.writeHead(200,{'Content-Type':asset.type}); return res.end(req.method==='HEAD'?undefined:asset.body); }
      if (req.method === 'GET' && route === '/source.zip') {
        if(!process.env.SOURCE_ARCHIVE) return json(503,{error:'이 배포의 소스 압축 파일이 아직 준비되지 않았어요.'});
        const source=await readFile(process.env.SOURCE_ARCHIVE);res.writeHead(200,{'Content-Type':'application/zip','Content-Disposition':'attachment; filename=Sasarisa-Windows-Source.zip'});return res.end(source);
      }
      if (req.method === 'GET' && route === '/api/info') return json(200,{mode:'internet',voice:true});
      if (req.method === 'GET' && route === '/health') return json(200,{ok:true,service:'sasarisa-relay',version:1});
      if (req.headers['x-sasarisa-client'] !== '1' || (req.method === 'POST' && !route.startsWith('/api/desktop/') && req.headers.origin !== origin)) return json(403,{error:'연결 화면에서 다시 요청하세요.'});
      const peer = req.headers['x-forwarded-for']?.split(',')[0].trim() || req.socket.remoteAddress;
      if (limits.size > 10000) return json(503,{error:'잠시 뒤 다시 연결하세요.'});
      const token = (req.headers.authorization || '').replace(/^Bearer /,'');
      if (route === '/api/desktop/open' && req.method === 'POST') {
        await body(req);
        if (!rate('open:'+peer,5,600000) || rooms.size >= 128) return json(429,{error:'연결을 너무 자주 만들었어요. 잠시 뒤 다시 시도하세요.'});
        let code; do { code = Array.from({length:12},()=>alphabet[randomInt(alphabet.length)]).join(''); } while(codes.has(code));
        const room = {token:secret(),code,pairUntil:clock()+600000,expires:clock()+28800000,seen:clock(),phones:new Set(),commands:new Map(),wait:null,polling:false,closed:false};
        rooms.set(room.token,room); codes.set(code,room);
        return json(200,{token:room.token,code,expires:room.expires,pairUntil:room.pairUntil});
      }
      if (route.startsWith('/api/desktop/')) {
        const room = rooms.get(token); if (!room) return json(401,{error:'PC 연결이 만료됐어요. 새 연결을 켜세요.'});
        room.seen = clock();
        if (route === '/api/desktop/close' && req.method === 'POST') { await body(req); close(room); return json(200,{ok:true}); }
        if (route === '/api/desktop/done' && req.method === 'POST') {
          const data = await body(req), row = room.commands.get(data.id);
          if (!row) return json(404,{error:'요청을 찾지 못했어요.'});
          if (typeof data.result !== 'string' || !data.result.trim() || data.result.length > 4000) return json(400,{error:'응답 형식을 확인하세요.'});
          if (!row.done) { row.done = true; row.result = data.result; row.text = ''; }
          return json(200,{ok:true});
        }
        if (route === '/api/desktop/poll' && req.method === 'GET') {
          if (room.polling) return json(409,{error:'이미 PC 연결을 확인 중이에요.'});
          room.polling = true;
          const ready = () => [...room.commands.values()].find(row => !row.done && !row.cancelled && row.sent + 10000 <= clock());
          try {
            if (!ready() && ![...room.commands.values()].some(row=>!row.done && row.cancelled)) await new Promise(resolve=>{
              let waitTimer;
              const finish = () => { clearTimeout(waitTimer); res.off('close',finish); if(room.wait===finish) room.wait=null; resolve(); };
              room.wait=finish; res.once('close',finish); waitTimer=setTimeout(finish,pollMs);
            });
            if (room.closed) return json(401,{error:'PC 연결이 종료됐어요.'});
            const row = ready(); if (row) row.sent = clock(); room.seen = clock();
            return json(200,{command:row ? {id:row.id,text:row.text} : null,cancelled:[...room.commands.values()].filter(row=>!row.done&&row.cancelled).map(row=>row.id),connected:room.phones.size});
          } finally { room.polling = false; }
        }
        return json(404,{error:'요청 주소를 확인하세요.'});
      }
      if (route === '/api/pair' && req.method === 'POST') {
        if (!rate('pair:'+peer,10)) return json(429,{error:'코드를 너무 많이 시도했어요. 1분 뒤 다시 시도하세요.'});
        const data = await body(req), code = typeof data.code === 'string' ? data.code.replace(/[ -]/g,'').toUpperCase() : '';
        const room = codes.get(code);
        if (!room || room.pairUntil <= clock() || room.seen + 45000 < clock()) return json(401,{error:'연결 코드와 PC 연결 상태를 확인하세요.'});
        if(room.phones.size>=4) return json(409,{error:'이 PC에는 휴대폰을 최대 4대까지 연결할 수 있어요.'});
        const key=secret(); phones.set(key,room); room.phones.add(key); wake(room);
        return json(200,{token:key,expires:room.expires});
      }
      const room = phones.get(token); if(!room) return json(401,{error:'연결이 만료됐어요. PC의 새 코드로 다시 연결하세요.'});
      if(route==='/api/status' && req.method==='GET') return json(200,{connected:room.seen+45000>=clock(),expires:room.expires});
      if(route==='/api/logout' && req.method==='POST') { await body(req); cancel(room,token); room.phones.delete(token); phones.delete(token); return json(200,{ok:true}); }
      if(route==='/api/stop' && req.method==='POST') { await body(req); cancel(room,token); return json(200,{message:'이 휴대폰의 요청에 중지를 전달했어요.'}); }
      if(route==='/api/result' && req.method==='GET') {
        const row=room.commands.get(url.searchParams.get('id'));
        if(!row || row.owner!==token) return json(404,{error:'이 휴대폰의 요청을 찾지 못했어요.'});
        return json(200,{pending:!row.done,result:row.done?row.result:''});
      }
      if(route==='/api/command' && req.method==='POST') {
        const data=await body(req);
        if(typeof data.id!=='string' || !idPattern.test(data.id) || !cleanText(data.text)) return json(400,{error:'명령은 300자 이내로 입력하세요.'});
        const old=room.commands.get(data.id);
        if(old) return old.owner!==token ? json(404,{error:'이 휴대폰의 요청을 찾지 못했어요.'}) : json(200,{pending:!old.done,result:old.done?old.result:''});
        if(!rate('command:'+token,12)) return json(429,{error:'요청을 너무 자주 보냈어요. 잠시 기다려 주세요.'});
        if(room.seen+45000<clock()) return json(409,{error:'PC 연결이 끊겨 있어요. PC에서 연결 상태를 확인하세요.'});
        if(room.commands.size>=1000) return json(409,{error:'이 연결의 요청 한도에 도달했어요. PC에서 새 연결을 만드세요.'});
        const busy=[...room.commands.values()].some(row=>!row.done);
        const row={id:data.id,text:data.text.trim(),owner:token,created:clock(),sent:0,cancelled:false,done:busy,result:busy?'PC에서 다른 휴대폰 요청을 처리 중이에요. 끝난 뒤 다시 요청하세요.':''};
        room.commands.set(row.id,row); wake(room);
        return json(200,{pending:!row.done,result:row.result});
      }
      json(404,{error:'요청 주소를 확인하세요.'});
    } catch(error) { json(error.status||500,{error:error.status ? error.message : '중계 서버에서 요청을 처리하지 못했어요.'}); }
  });
  server.requestTimeout=15000; server.headersTimeout=10000; server.maxConnections=512;
  server.on('close',()=>{clearInterval(timer);for(const room of rooms.values())close(room);});
  return server;
}

if(process.argv[1] && path.resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  const server=await createRelay({origin:process.env.PUBLIC_ORIGIN,webRoot:process.env.WEB_ROOT});
  server.listen(Number(process.env.PORT||17846),'127.0.0.1',()=>console.log('Sasarisa HTTPS relay listener ready'));
  const stop=()=>{server.close();server.closeAllConnections();}; process.on('SIGTERM',stop);process.on('SIGINT',stop);
}
