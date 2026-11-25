import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const mode = process.argv[2] ?? 'core';

function run(command, args, cwd = root) {
  const result = spawnSync(command, args, { cwd, stdio: 'inherit', shell: false });
  assert.equal(result.status, 0, `${command} ${args.join(' ')} failed`);
}

function read(relativePath) {
  return readFileSync(path.join(root, relativePath), 'utf8');
}

function versionMetadata() {
  const manifest = JSON.parse(read('vscode-extension/package.json'));
  const cargo = read('rust/zuma-mcp/Cargo.toml');
  const lock = read('rust/zuma-mcp/Cargo.lock');
  assert.equal(manifest.version, '0.14.0');
  assert.match(cargo, /version\s*=\s*"0\.14\.0"/);
  assert.match(lock, /name = "zuma-mcp"\s+version = "0\.14\.0"/);
}

function docs() {
  for (const relativePath of ['README.md', 'vscode-extension/README.md']) {
    const text = read(relativePath);
    const releases = [...text.matchAll(/^## What's new in 0\.(\d+)\.(\d+)$/gm)].map(match => match[1]);
    assert.deepEqual(releases, ['14', '13', '12'], `${relativePath} must keep the latest three minor releases`);
    assert.match(text, /Available Tools \(33 Tools\)/);
    assert.match(text, /remember_context/);
    assert.match(text, /compile_context/);
    assert.match(text, /plan_context_read/);
    assert.match(text, /blob\/main\/CHANGELOG\.md/);
  }
  assert.match(read('CHANGELOG.md'), /^## 0\.14\.0$/m);
  assert.match(read('docs/reference.md'), /^## Context engine$/m);
}

if (mode === 'core') {
  run('dotnet', ['build', 'InazumaPwsh.csproj', '--no-restore', '--nologo', '-v:q']);
  run(process.execPath, ['test-mcp.mjs']);
  console.log('CONTEXT_CORE_OK');
} else if (mode === 'rust') {
  run('cargo', ['test', '--release', '--manifest-path', 'rust/zuma-mcp/Cargo.toml']);
  console.log('CONTEXT_RUST_OK');
} else if (mode === 'package') {
  const target = process.platform === 'win32' ? 'win32-x64'
    : process.platform === 'darwin' ? (process.arch === 'arm64' ? 'darwin-arm64' : 'darwin-x64')
      : (process.arch === 'arm64' ? 'linux-arm64' : 'linux-x64');
  run(process.execPath, ['vscode-extension/build-target.mjs', target]);
  const executable = process.platform === 'win32' ? 'vscode-extension/server/win-x64/InazumaPwsh.exe'
    : process.platform === 'darwin' ? `vscode-extension/server/osx-${process.arch === 'arm64' ? 'arm64' : 'x64'}/InazumaPwsh`
      : `vscode-extension/server/linux-${process.arch === 'arm64' ? 'arm64' : 'x64'}/InazumaPwsh`;
  run(process.execPath, ['rust/zuma-mcp/tests/real-smoke.mjs', executable]);
  console.log('CONTEXT_PACKAGE_OK');
} else if (mode === 'docs') {
  docs();
  console.log('CONTEXT_DOCS_OK');
} else if (mode === 'release') {
  versionMetadata();
  docs();
  run('git', ['diff', '--check']);
  console.log('CONTEXT_RELEASE_OK');
} else {
  throw new Error(`Unknown mode: ${mode}`);
}
