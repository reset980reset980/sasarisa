import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import {readFile} from 'node:fs/promises';
import {webcrypto} from 'node:crypto';

const source=await readFile(new URL('../app/VoiceAssistant.Desktop/PhoneWeb/phone.js',import.meta.url),'utf8');
async function fixture(t,{internet=true,secure=true,storageBlocked=false,client={kind:'standard',version:'0.2.28'}}={}) {
  const elements=new Map(), documentEvents={}, windowEvents={}, calls=[], recognizers=[];
  class Element {
    value='';textContent='';children=[];hidden=false;disabled=false;handlers={};attributes={};
    addEventListener(type,fn){this.handlers[type]=fn;}
    append(item){this.children.push(item);} setAttribute(name,value){this.attributes[name]=value;}
    focus(){} get firstElementChild(){return{remove:()=>this.children.shift()};}
  }
  const element=id=>{if(!elements.has(id))elements.set(id,new Element());return elements.get(id);};
  class Speech {
    constructor(){recognizers.push(this);} start(){this.started=true;this.onstart?.();}
    stop(){this.stopped=true;this.onend?.();} abort(){this.aborted=true;this.onend?.();}
  }
  const document={hidden:false,getElementById:element,querySelectorAll:()=>[],createElement:()=>new Element(),addEventListener:(type,fn)=>documentEvents[type]=fn};
  const storage=new Map();
  const context=vm.createContext({document,window:{SpeechRecognition:Speech,isSecureContext:secure,addEventListener:(type,fn)=>windowEvents[type]=fn},sessionStorage:{getItem:key=>{if(storageBlocked)throw Error();return storage.get(key);},setItem:(key,value)=>{if(storageBlocked)throw Error();storage.set(key,value);},removeItem:key=>storage.delete(key)},crypto:webcrypto,AbortController,setTimeout,clearTimeout,
    fetch:async(route,options)=>{calls.push({route,options});const data=route==='/api/info'?{mode:internet?'internet':'local',voice:internet}:route==='/api/pair'?{token:'test-phone-token',client}:route==='/api/status'?{connected:true,client}:route==='/api/command'?{pending:false,result:'clock fixture'}:{message:'stopped'};return{ok:true,status:200,json:async()=>data};}});
  vm.runInContext(source,context); const settle=()=>new Promise(resolve=>setImmediate(resolve)); await settle();
  const fire=async(id,type='click')=>{await element(id).handlers[type]({preventDefault(){}});await settle();};
  async function pair(){element('code').value='ABCD2345EFGH';await fire('pairForm','submit');}
  t.after(()=>windowEvents.pagehide());
  return{element,fire,pair,recognizers,calls,document,documentEvents,windowEvents};
}
test('microphone starts only after pairing and explicit click; transcript waits for Send',async t=>{
  const f=await fixture(t);assert.equal(f.recognizers.length,0);await f.fire('mic');assert.equal(f.recognizers.length,0);
  await f.pair();await f.fire('mic');const speech=f.recognizers[0];assert.equal(speech.lang,'ko-KR');assert.equal(f.element('send').disabled,true);
  speech.onresult({results:[[{transcript:'지금 몇 시야'}]]});speech.onend();
  assert.equal(f.element('command').value,'지금 몇 시야');assert.equal(f.calls.filter(x=>x.route==='/api/command').length,0);
  await f.fire('commandForm','submit');assert.equal(f.calls.filter(x=>x.route==='/api/command').length,1);
});
test('hiding page aborts recognition; late callbacks cannot change draft',async t=>{
  const f=await fixture(t);await f.pair();await f.fire('mic');const speech=f.recognizers[0];
  f.document.hidden=true;f.documentEvents.visibilitychange();assert.equal(speech.aborted,true);
  speech.onresult({results:[[{transcript:'late command'}]]});assert.equal(f.element('command').value,'');assert.equal(f.element('command').readOnly,false);
});
test('permission failure leaves a useful message and never restarts or sends',async t=>{
  const f=await fixture(t);await f.pair();await f.fire('mic');const speech=f.recognizers[0];speech.onerror({error:'not-allowed'});speech.onend();
  assert.match(f.element('notice').textContent,/직접 허용/);assert.equal(f.recognizers.length,1);assert.equal(f.element('mic').disabled,false);
  assert.equal(f.calls.filter(x=>x.route==='/api/command').length,0);
});
test('LAN or insecure pages cannot start Web Speech',async t=>{
  for(const options of [{internet:false},{secure:false}]) {const f=await fixture(t,options);await f.pair();await f.fire('mic');assert.equal(f.element('mic').disabled,true);assert.equal(f.recognizers.length,0);}
});
test('long transcript is kept for editing and is not submitted',async t=>{
  const f=await fixture(t);await f.pair();await f.fire('mic');const speech=f.recognizers[0];speech.onresult({results:[[{transcript:'가'.repeat(301)}]]});speech.onend();
  await f.fire('commandForm','submit');assert.match(f.element('notice').textContent,/300자/);assert.equal(f.calls.filter(x=>x.route==='/api/command').length,0);
});
test('unavailable session storage does not break pairing or typed commands',async t=>{
  const f=await fixture(t,{storageBlocked:true});await f.pair();f.element('command').value='지금 몇 시야';await f.fire('commandForm','submit');
  assert.equal(f.calls.filter(x=>x.route==='/api/command').length,1);assert.equal(f.element('notice').textContent,'완료');
});

test('preview pairing prominently explains clock-only and muted PC, normal pairing lists real commands',async t=>{
  for(const kind of ['preview','standard']) {
    const f=await fixture(t,{client:{kind,version:'0.2.28'}});await f.pair();
    assert.match(f.element('connectionName').textContent,/0\.2\.28/);
    if(kind==='preview') {assert.match(f.element('connectionDetail').textContent,/시계 명령만/);assert.match(f.element('connectionDetail').textContent,/PC 음성은 꺼져/);assert.equal(f.element('connectionDetail').className,'connection-warning');}
    else {assert.match(f.element('connectionDetail').textContent,/타이머·앱 실행/);assert.equal(f.element('connectionDetail').className,'hint');}
  }
});

test('missing profile never presents a legacy preview as a normal connection',async t=>{
  const f=await fixture(t,{client:null});await f.pair();assert.match(f.element('connectionName').textContent,/종류 확인 필요/);assert.match(f.element('connectionDetail').textContent,/새 연결 코드/);
});
