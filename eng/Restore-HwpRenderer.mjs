import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, renameSync, rmSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const lock = JSON.parse(readFileSync(new URL('./HwpRenderer.lock.json', import.meta.url), 'utf8'));
const cache = fileURLToPath(new URL(`../artifacts/rhwp-${lock.version}/`, import.meta.url));
export const packageDirectory = path.join(cache, 'package');

// Hash the path and fixed-size content digest of each file in canonical order,
// including all upstream notices. A previous build can then restore offline.
function packageIsValid(directory) {
    if (!existsSync(directory)) return false;
    const hash = createHash('sha256');
    const entries = readdirSync(directory, { recursive: true }).map(name => name.split(path.sep).join('/')).sort();
    for (const entry of entries) {
        const filename = path.join(directory, entry);
        const stat = lstatSync(filename);
        if (stat.isDirectory()) continue;
        if (!stat.isFile()) return false;
        hash.update(entry + '\0');
        hash.update(createHash('sha256').update(readFileSync(filename)).digest());
    }
    return hash.digest('hex') === lock.extractedSha256;
}

export async function restoreHwpRenderer() {
    if (packageIsValid(packageDirectory)) return packageDirectory;
    const response = await fetch(lock.tarball, { signal: AbortSignal.timeout(120_000) });
    if (!response.ok) throw new Error(`HWP renderer download failed (HTTP ${response.status}).`);
    const bytes = Buffer.from(await response.arrayBuffer());
    const integrity = 'sha512-' + createHash('sha512').update(bytes).digest('base64');
    if (integrity !== lock.integrity) throw new Error('HWP renderer npm archive integrity mismatch.');

    mkdirSync(cache, { recursive: true });
    const staging = mkdtempSync(path.join(cache, 'restore-'));
    try {
        const extract = spawnSync('tar', ['-xz', '-f', '-', '-C', staging], { input: bytes });
        if (extract.error) throw extract.error;
        if (extract.status !== 0) throw new Error(`HWP renderer extraction failed: ${extract.stderr}`);
        const unpacked = path.join(staging, 'package');
        if (!packageIsValid(unpacked)) throw new Error('Extracted HWP renderer integrity mismatch.');
        // A concurrent build may have published its own verified copy first.
        if (!packageIsValid(packageDirectory)) {
            if (existsSync(packageDirectory)) renameSync(packageDirectory, path.join(staging, 'previous'));
            try {
                renameSync(unpacked, packageDirectory);
            } catch (error) {
                if (!packageIsValid(packageDirectory)) throw error;
            }
        }
    } finally {
        if (path.dirname(staging) === path.resolve(cache) && path.basename(staging).startsWith('restore-')) {
            rmSync(staging, { recursive: true, force: true });
        }
    }
    return packageDirectory;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
    // MSBuild consumes the absolute path on stdout.
    console.log(await restoreHwpRenderer());
}
