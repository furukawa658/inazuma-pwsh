// Usage: node vscode-extension/build-target.mjs <win32-x64|linux-x64|linux-arm64|darwin-x64|darwin-arm64>
import { spawnSync } from 'node:child_process';
import { readFileSync, rmSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const rids = { 'win32-x64': 'win-x64', 'linux-x64': 'linux-x64', 'linux-arm64': 'linux-arm64', 'darwin-x64': 'osx-x64', 'darwin-arm64': 'osx-arm64' };
const target = process.argv[2];
const rid = rids[target];
if (!rid) { console.error(`Unknown target "${target}". Use one of: ${Object.keys(rids).join(', ')}`); process.exit(2); }

const extensionDir = path.dirname(fileURLToPath(import.meta.url));
const root = path.dirname(extensionDir);
const version = JSON.parse(readFileSync(path.join(extensionDir, 'package.json'), 'utf8')).version;
const serverDir = path.join(extensionDir, 'server');
const shell = process.platform === 'win32';

function run(command, args, cwd) {
    console.log(`> ${command} ${args.join(' ')}`);
    const result = spawnSync(command, args, { cwd, stdio: 'inherit', shell });
    if (result.status !== 0) { console.error(`Failed: ${command} (exit ${result.status})`); process.exit(result.status ?? 1); }
}

// Keep only this target's server so every VSIX bundles a single binary.
rmSync(serverDir, { recursive: true, force: true });
run('dotnet', ['publish', 'InazumaPwsh.csproj', '-c', 'Release', '-r', rid, '--nologo', '-v:q',
    '-o', path.join(extensionDir, 'server', rid)], root);
run('npx', ['vsce', 'package', '--target', target, '--no-dependencies', '--out',
    path.join(root, 'artifacts', `zuma-mcp-${version}-${target}.vsix`)], extensionDir);