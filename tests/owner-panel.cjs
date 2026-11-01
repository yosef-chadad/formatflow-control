const fs=require('fs'),vm=require('vm'),assert=require('assert/strict'),crypto=require('crypto');
const html=fs.readFileSync(__dirname+'/../index.html','utf8');
let code=html.match(/<script>([\s\S]*?)<\/script>/)[1];
new vm.Script(code);
code=code.replace('reload();\n})();','globalThis.test={state,save,connect,mergeAd,dimensions,https,text64,decode64,loadForm};\n})();');
class Element {
 constructor(){this.value='';this.textContent='';this.dataset={};this.options=[];this.hidden=false;this.disabled=false;this.classList={toggle(){}};this.events={};}
 addEventListener(name,fn){this.events[name]=fn;}setAttribute(k,v){this[k]=v;}removeAttribute(k){delete this[k];}replaceChildren(...items){this.options=items;}add(item){this.options.push(item);}showModal(){this.open=true;}close(){this.open=false;}focus(){}click(){}
}
const elements={},slots=['top','belowSearch'].map(slot=>{const b=new Element();b.dataset.slot=slot;return b;});
const fixture={schema:1,revision:8,policy:{enabled:false,message:'מושהה'},appearance:{title:'Welcome',subtitle:'שלום'},ads:[{image:'https://example.com/top.png',link:'',placement:'top'}],latest:{version:'4.0.0',url:'https://example.com/application.exe',sha256:'a'.repeat(64),installMode:'ask'},future:{untouched:true}};
let remote=structuredClone(fixture),remoteSha='sha-8',calls=[],rejectPut=false;
const context={console,URL,TextEncoder,TextDecoder,Uint8Array,DataView,Blob,AbortController,crypto:crypto.webcrypto,setTimeout,clearTimeout,Option:function(text,value){this.text=text;this.value=value;},btoa:x=>Buffer.from(x,'binary').toString('base64'),atob:x=>Buffer.from(x,'base64').toString('binary'),confirm:()=>true,document:{getElementById:id=>elements[id]||(elements[id]=new Element()),querySelectorAll:()=>slots},window:{addEventListener(){}},fetch:async(url,options)=>{
 calls.push({url,options});assert(url.startsWith('https://api.github.com/repos/yosef-chadad/formatflow-control/contents/'));
 const response=(data,status=200)=>({ok:status<400,status,json:async()=>data});
 if(options.method==='PUT'){
  if(rejectPut)return response({},403);const body=JSON.parse(options.body);
  if(url.endsWith('/feed.json')){assert.equal(body.sha,remoteSha);remote=JSON.parse(Buffer.from(body.content,'base64').toString('utf8'));remoteSha='sha-'+remote.revision;return response({content:{sha:remoteSha}});}
  assert(!body.sha);assert(url.includes('ad-'));return response({content:{sha:'image-sha'}},201);
 }
 if(url.includes('/feed.json?'))return response({sha:remoteSha,content:Buffer.from(JSON.stringify(remote)).toString('base64')});
 return response([]);
}};
vm.createContext(context);vm.runInContext(code,context);const t=context.test,el=id=>context.document.getElementById(id);
let count=0;function check(name,condition){assert(condition,name);count++;console.log('PASS',name);}
function reset(){remote=structuredClone(fixture);remoteSha='sha-8';calls=[];rejectPut=false;t.state.feed=structuredClone(fixture);t.state.slot='belowSearch';t.state.token='';t.state.file=null;t.state.blob='';t.state.busy=false;t.state.imageReady=true;el('imageUrl').value='https://example.com/banner.png';el('targetUrl').value='https://advertiser.example/offer';}
const writes=()=>calls.filter(c=>c.options.method==='PUT');
(async()=>{
reset();await t.save();check('unauthenticated save opens sign-in without writes',el('authDialog').open&&writes().length===0);
el('tokenInput').value='synthetic-test-token';await t.connect({preventDefault(){}});check('connect clears password field',el('tokenInput').value===''&&t.state.token==='synthetic-test-token');check('connect does not publish',writes().length===0);
t.state.imageReady=true;await t.save();check('publish commits one feed change',writes().length===1&&remote.revision===9);check('image and destination published',remote.ads.find(a=>a.placement==='belowSearch').link==='https://advertiser.example/offer');check('other slot preserved',remote.ads.find(a=>a.placement==='top').image===fixture.ads[0].image);check('policy release and unknown fields preserved',JSON.stringify(remote.policy)===JSON.stringify(fixture.policy)&&JSON.stringify(remote.latest)===JSON.stringify(fixture.latest)&&remote.future.untouched);check('token only in API header',calls.every(c=>c.options.headers.Authorization==='Bearer synthetic-test-token')&&!writes().some(c=>c.options.body.includes('synthetic-test-token')||Buffer.from(JSON.parse(c.options.body).content,'base64').toString().includes('synthetic-test-token')));
calls=[];await t.save(true);check('remove deletes only selected slot',remote.ads.length===1&&remote.ads[0].placement==='top');check('remove does not delete image file',calls.every(c=>c.options.method!=='DELETE')&&writes().length===1);
reset();t.state.token='synthetic-test-token';remote.ads.push({placement:'belowSearch',image:'https://other.example/new.png'});await t.save();check('concurrent same-slot edit is not overwritten',writes().length===0&&el('status').dataset.kind==='error');
reset();t.state.token='synthetic-test-token';remote.ads[0].link='https://changed.example';await t.save();check('concurrent other-slot edit is retained',remote.ads[0].link==='https://changed.example');
reset();t.state.token='synthetic-test-token';el('targetUrl').value='javascript:alert(1)';await t.save();check('javascript destination rejected before network',calls.length===0);
reset();t.state.token='synthetic-test-token';el('imageUrl').value='http://example.com/banner.png';await t.save();check('non-HTTPS image rejected before network',calls.length===0);
reset();t.state.token='synthetic-test-token';t.state.imageReady=false;await t.save();check('unloaded image is not published',calls.length===0);
reset();t.state.token='synthetic-test-token';rejectPut=true;await t.save();check('permission failure does not claim success',el('status').dataset.kind==='error'&&t.state.feed.revision===8&&!t.state.busy);
reset();t.state.token='synthetic-test-token';el('imageUrl').value='';const bytes=Uint8Array.from(Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aK1sAAAAASUVORK5CYII=','base64'));t.state.file={bytes,ext:'png'};await t.save();
check('local upload precedes feed update',writes().length===2&&!writes()[0].url.endsWith('feed.json')&&writes()[1].url.endsWith('feed.json'));
check('uploaded image points to own repository',remote.ads.find(a=>a.placement==='belowSearch').image.startsWith('https://raw.githubusercontent.com/yosef-chadad/formatflow-control/main/ad-'));
check('PNG dimensions accepted',t.dimensions(bytes).w===1);const huge=bytes.slice();huge[16]=127;check('huge raster rejected',!t.dimensions(huge));check('HTML image rejected',!t.dimensions(new TextEncoder().encode('<html><body>hello</body></html>')));check('credentialled URL rejected',!t.https('https://user:password@example.com/'));
const hebrew='רוצים לפרסם כאן!';check('Hebrew roundtrip',t.decode64(t.text64(hebrew))===hebrew);
check('no persistent token storage or HTML injection',!/(?:localStorage|sessionStorage|innerHTML|eval\()/.test(code));
const csp=html.match(/http-equiv="Content-Security-Policy" content="([^"]+)"/)[1];const hash=crypto.createHash('sha256').update(html.match(/<script>([\s\S]*?)<\/script>/)[1]).digest('base64');check('only two placement buttons',!html.includes('data-slot="bottom"'));
check('CSP pins exact script',csp.includes("'sha256-"+hash+"'")&&!csp.includes("script-src 'unsafe-inline'"));
const legacy=structuredClone(fixture);legacy.ads.push({placement:'bottom',image:'https://old.example/banner.png'});const merged=t.mergeAd(legacy,'belowSearch',{placement:'belowSearch',image:'https://new.example/banner.png'});check('lower edit replaces legacy bottom slot',merged.ads.length===2&&!merged.ads.some(a=>a.placement==='bottom'));
console.log(count+' owner-panel checks passed. All writes were mocked; no production feed changed.');
})().catch(error=>{console.error(error);process.exitCode=1;});
