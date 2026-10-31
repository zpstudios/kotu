import { readFile, mkdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
// 외부 의존성은 고정 해시의 아카이브만 사용한다. 설치 스크립트와 네트워크 실행은 없다.
const archiveRoot = resolve(process.argv[2] ?? '.');
process.chdir(fileURLToPath(new URL('.', import.meta.url)));
for (const item of JSON.parse(await readFile('source-lock.json', 'utf8'))) {
  const archive = resolve(archiveRoot, item.archive);
  const hash = `sha512-${createHash('sha512').update(await readFile(archive)).digest('base64')}`;
  if (hash !== item.integrity) throw new Error(`Integrity mismatch: ${item.name}`);
  const target = resolve('node_modules', item.name);
  await mkdir(target, { recursive: true });
  execFileSync('tar', ['-xf', archive, '-C', target, '--strip-components=1']);
}
