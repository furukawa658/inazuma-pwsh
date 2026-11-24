import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const mode = process.argv[2] ?? 'release';

function run(command, args) {
  const result = spawnSync(command, args, { cwd: root, stdio: 'inherit', shell: false });
  assert.equal(result.status, 0, `${command} ${args.join(' ')} failed`);
}

if (mode === 'rust') {
  run('cargo', ['test', '--release', '--manifest-path', 'rust/zuma-mcp/Cargo.toml']);
  console.log('HANDOFF_TESTS_OK');
} else if (mode === 'release') {
  run('dotnet', ['build', 'InazumaPwsh.csproj', '--no-restore', '--nologo', '-v:q']);
  run(process.execPath, ['vscode-extension/test-extension.cjs']);
  const manifest = JSON.parse(readFileSync(path.join(root, 'vscode-extension/package.json'), 'utf8'));
  const cargo = readFileSync(path.join(root, 'rust/zuma-mcp/Cargo.toml'), 'utf8');
  assert.equal(manifest.version, '0.11.0');
  assert.equal(manifest.displayName, 'Zuma MCP: Context & Log Optimizer for AI Coding');
  assert.equal(manifest.contributes.configuration.properties['inazumaPwsh.handoff.enabled'].default, false);
  assert.match(cargo, /version\s*=\s*"0\.11\.0"/);
  assert.match(readFileSync(path.join(root, 'README.md'), 'utf8'), /Available Tools \(23 Tools\)/);
  assert.match(readFileSync(path.join(root, 'rust/zuma-mcp/src/main.rs'), 'utf8'), /prepare_handoff/);
  console.log('HANDOFF_RELEASE_CHECKS_OK');
} else if (mode === 'package') {
  const target = process.platform === 'win32' ? 'win32-x64'
    : process.platform === 'darwin' ? (process.arch === 'arm64' ? 'darwin-arm64' : 'darwin-x64')
      : (process.arch === 'arm64' ? 'linux-arm64' : 'linux-x64');
  run(process.execPath, ['vscode-extension/build-target.mjs', target]);
  const executable = process.platform === 'win32' ? 'vscode-extension/server/win-x64/InazumaPwsh.exe'
    : process.platform === 'darwin' ? `vscode-extension/server/osx-${process.arch === 'arm64' ? 'arm64' : 'x64'}/InazumaPwsh`
      : `vscode-extension/server/linux-${process.arch === 'arm64' ? 'arm64' : 'x64'}/InazumaPwsh`;
  run(process.execPath, ['rust/zuma-mcp/tests/real-smoke.mjs', executable]);
  console.log('PACKAGE_OK');
} else {
  throw new Error(`Unknown mode: ${mode}`);
}
