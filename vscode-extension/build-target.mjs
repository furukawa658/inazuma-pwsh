// Usage: node vscode-extension/build-target.mjs <win32-x64|linux-x64|linux-arm64|darwin-x64|darwin-arm64>
import { spawnSync } from 'node:child_process';
import { copyFileSync, mkdirSync, readFileSync, renameSync, rmSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const rids = { 'win32-x64': 'win-x64', 'linux-x64': 'linux-x64', 'linux-arm64': 'linux-arm64', 'darwin-x64': 'osx-x64', 'darwin-arm64': 'osx-arm64' };
const target = process.argv[2];
const rid = rids[target];
if (!rid) { console.error(`Unknown target "${target}". Use one of: ${Object.keys(rids).join(', ')}`); process.exit(2); }
const nativeTarget = process.platform === 'win32' ? 'win32-x64'
    : process.platform === 'darwin' ? (process.arch === 'arm64' ? 'darwin-arm64' : 'darwin-x64')
    : (process.arch === 'arm64' ? 'linux-arm64' : 'linux-x64');
if (target !== nativeTarget) {
    console.error(`The ${target} Rust server must be built on its matching host (${nativeTarget} detected).`);
    process.exit(2);
}

const extensionDir = path.dirname(fileURLToPath(import.meta.url));
const root = path.dirname(extensionDir);
const version = JSON.parse(readFileSync(path.join(extensionDir, 'package.json'), 'utf8')).version;
mkdirSync(path.join(root, 'artifacts'), { recursive: true });
const serverDir = path.join(extensionDir, 'server');

function run(command, args, cwd) {
    console.log(`> ${command} ${args.join(' ')}`);
    let executable = command;
    let commandArgs = args;
    if (command === 'npx' && process.platform === 'win32') {
        executable = process.execPath;
        commandArgs = [path.join(path.dirname(process.execPath), 'node_modules', 'npm', 'bin', 'npx-cli.js'), ...args];
    }
    const result = spawnSync(executable, commandArgs, { cwd, stdio: 'inherit', shell: false });
    if (result.status !== 0) { console.error(`Failed: ${command} (exit ${result.status})`); process.exit(result.status ?? 1); }
}

// Keep only this target's server files so each VSIX contains one runtime target.
rmSync(serverDir, { recursive: true, force: true });
const runtimeDir = path.join(extensionDir, 'server', rid);
run('dotnet', ['publish', 'InazumaPwsh.csproj', '-c', 'Release', '-r', rid, '--nologo', '-v:q',
    '-o', runtimeDir], root);
const rustManifest = path.join(root, 'rust', 'zuma-mcp', 'Cargo.toml');
run('cargo', ['build', '--release', '--manifest-path', rustManifest], root);
const executableExtension = process.platform === 'win32' ? '.exe' : '';
const publishedExecutable = path.join(runtimeDir, `InazumaPwsh${executableExtension}`);
const coreExecutable = path.join(runtimeDir, `InazumaPwsh.core${executableExtension}`);
renameSync(publishedExecutable, coreExecutable);
copyFileSync(path.join(root, 'rust', 'zuma-mcp', 'target', 'release', `zuma-mcp${executableExtension}`),
    publishedExecutable);
run('npx', ['vsce', 'package', '--target', target, '--no-dependencies', '--out',
    path.join(root, 'artifacts', `zuma-mcp-${version}-${target}.vsix`)], extensionDir);