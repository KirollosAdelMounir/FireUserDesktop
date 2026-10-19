// Development-only vector generator. Uses the same JOSE package as the Angular client.
// All generated keys are disposable test identities, never deployment credentials.
import { webcrypto } from 'node:crypto';
import { writeFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
const jose = await import(pathToFileURL(process.argv[2]).href);
const keys = await webcrypto.subtle.generateKey({name:'RSA-OAEP',modulusLength:2048,publicExponent:new Uint8Array([1,0,1]),hash:'SHA-256'},true,['encrypt','decrypt']);
const signing = await jose.generateKeyPair('ES256', {extractable:true});
const userId = '11111111-1111-4111-8111-111111111111';
const publicIdentity = {encryptionKey:await webcrypto.subtle.exportKey('jwk',keys.publicKey),signingKey:await jose.exportJWK(signing.publicKey)};
const payload = {version:1,nonce:'test-vector',sender:userId,receiver:userId,conversationId:null,audience:[userId],text:'Native chat · مرحباً',files:[]};
const signed = await new jose.CompactSign(new TextEncoder().encode(JSON.stringify(payload))).setProtectedHeader({alg:'ES256',kid:userId,typ:'FUCHAT1'}).sign(signing.privateKey);
const encrypted = new jose.GeneralEncrypt(new TextEncoder().encode(signed)).setProtectedHeader({alg:'RSA-OAEP-256',enc:'A256GCM',typ:'FUCHAT1'});
encrypted.addRecipient(keys.publicKey).setUnprotectedHeader({kid:userId});
const recovery = {userId,publicKey:JSON.stringify(publicIdentity),encryptionPrivateKey:Buffer.from(await webcrypto.subtle.exportKey('pkcs8',keys.privateKey)).toString('base64'),signingPrivateKey:Buffer.from(await webcrypto.subtle.exportKey('pkcs8',signing.privateKey)).toString('base64')};
const file = {id:'22222222-2222-4222-8222-222222222222',name:'note.txt',mimeType:'text/plain',size:5,chunkSize:1048576,key:Buffer.from(new Uint8Array(32).fill(7)).toString('base64'),noncePrefix:Buffer.from(new Uint8Array(8).fill(3)).toString('base64')};
const aes = await webcrypto.subtle.importKey('raw',Buffer.from(file.key,'base64'),'AES-GCM',false,['encrypt']);
const iv = new Uint8Array(12);iv.set(Buffer.from(file.noncePrefix,'base64'));
const cipher = await webcrypto.subtle.encrypt({name:'AES-GCM',iv,additionalData:new TextEncoder().encode(JSON.stringify(['FUATT',1,file.id,0,file.size,file.chunkSize]))},aes,new TextEncoder().encode('hello'));
await writeFile(process.argv[3],JSON.stringify({recovery,publicIdentity,wire:'FU1.'+JSON.stringify(await encrypted.encrypt()),file,fileCipher:Buffer.from(cipher).toString('base64')},null,2));
