import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { inflateSync } from 'node:zlib';
import { unityBundleFixture } from './fixtures/unity-bundle-fixture.mjs';

function decode(png) {
    let at=8, width, height; const data=[];
    while(at<png.length){const n=png.readUInt32BE(at),kind=png.toString('ascii',at+4,at+8),bytes=png.subarray(at+8,at+8+n);if(kind==='IHDR'){width=bytes.readUInt32BE();height=bytes.readUInt32BE(4);assert.equal(bytes[8],8);assert.equal(bytes[9],6);assert.equal(bytes[12],0);}if(kind==='IDAT')data.push(bytes);at+=n+12;}
    const raw=inflateSync(Buffer.concat(data)), out=Buffer.alloc(width*height*4), stride=width*4;
    const paeth=(a,b,c)=>{const p=a+b-c,da=Math.abs(p-a),db=Math.abs(p-b),dc=Math.abs(p-c);return da<=db&&da<=dc?a:db<=dc?b:c;};
    for(let y=0;y<height;y++)for(let x=0;x<stride;x++){const a=x>=4?out[y*stride+x-4]:0,b=y?out[(y-1)*stride+x]:0,c=y&&x>=4?out[(y-1)*stride+x-4]:0,f=raw[y*(stride+1)],r=raw[y*(stride+1)+1+x];assert.ok(f<=4);out[y*stride+x]=(r+[0,a,b,Math.floor((a+b)/2),paeth(a,b,c)][f])&255;}
    return {width,height,pixels:out};
}
const root=path.resolve('.tools/snr-release/fixture');await fs.mkdir(root,{recursive:true});
const scenarios=[{packageName:'fixed',adaptive:false},{packageName:'adaptive',adaptive:true},{packageName:'visor',visor:true,adaptive:true,isSNR:true}];
for(const s of scenarios){const dir=path.join(root,'SuperNewRolesNext','CustomCosmetics',s.packageName+'.bundle');await fs.mkdir(dir,{recursive:true});await fs.writeFile(path.join(dir,s.packageName+'.bundle'),unityBundleFixture({...s,packedSprite:true}).bundle);}
const child=spawn(path.resolve('artifacts/snr-cosmetics/tbcl-snr-cosmetics.exe'),[],{windowsHide:true,stdio:['pipe','pipe','pipe']});
const lines=createInterface({input:child.stdout})[Symbol.asyncIterator]();
async function request(id,part){child.stdin.write(JSON.stringify({root,id,part})+'\n');const next=await Promise.race([lines.next(),new Promise((_,reject)=>{const timer=setTimeout(()=>reject(Error('Native request timeout')),10000);timer.unref();})]);assert.ok(!next.done);return JSON.parse(next.value);}
try {
    for(const s of scenarios){const r=await request('Modded_'+s.packageName+'_hat',s.visor?'visor':'hat-front');assert.ok(!r.error,r.error);assert.equal(r.adaptive,s.adaptive);assert.equal(r.isSNR,!!s.isSNR);const image=decode(Buffer.from(r.image,'base64'));assert.deepEqual([image.width,image.height],[8,6]);const fixture=unityBundleFixture(s),expected=Buffer.alloc(8*6*4);for(let y=0;y<2;y++)for(let x=0;x<2;x++)fixture.pixels.copy(expected,((y+3)*8+x+2)*4,((1-y)*2+x)*4,((1-y)*2+x+1)*4);assert.deepEqual(image.pixels,expected);}
    const missing=await request('Modded_retry_Item','hat-front');assert.ok(missing.error);
    const dir=path.join(root,'SuperNewRolesNext','CustomCosmetics','retry');await fs.mkdir(dir,{recursive:true});
    const good=await request('Modded_fixed_hat','hat-front');await fs.writeFile(path.join(dir,'Item_front.png'),Buffer.from(good.image,'base64'));
    const recovered=await request('Modded_retry_Item','hat-front');assert.equal(recovered.image,good.image);assert.equal(recovered.adaptive,null);
    console.log('[PASS] Native SNR packed full-canvas RGBA pixels, per-part adaptive/IsSNR metadata and missing-file recovery on one cached worker');
} finally {child.stdin.end();child.kill();}
