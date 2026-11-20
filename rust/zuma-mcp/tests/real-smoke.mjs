import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { performance } from 'node:perf_hooks';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const hostRid = process.platform === 'win32' ? 'win-x64'
  : process.platform === 'darwin' ? (process.arch === 'arm64' ? 'osx-arm64' : 'osx-x64')
  : (process.arch === 'arm64' ? 'linux-arm64' : 'linux-x64');
const executable = process.argv[2] ?? path.join(root, 'bin/Debug/net10.0', hostRid,
  process.platform === 'win32' ? 'InazumaPwsh.exe' : 'InazumaPwsh');

const child = spawn(executable, [], {
  cwd: root,
  stdio: ['pipe', 'pipe', 'pipe'],
  env: { ...process.env, ZUMA_WORKSPACE_ROOT: root }
});
const pending = new Map();
let nextId = 1;
let stderr = '';
child.stderr.on('data', chunk => { stderr += chunk; });
createInterface({ input: child.stdout }).on('line', line => {
  const message = JSON.parse(line);
  const waiter = pending.get(message.id);
  if (!waiter) return;
  pending.delete(message.id);
  clearTimeout(waiter.timer);
  waiter.resolve(message);
});

function send(method, params, id) {
  child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', ...(id === undefined ? {} : { id }), method, params })}\n`);
}

function request(method, params, timeoutMs = 120000) {
  const id = nextId++;
  const response = new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`No response for ${method}: ${stderr}`)), timeoutMs);
    pending.set(id, { resolve, reject, timer });
  });
  send(method, params, id);
  return { id, response };
}

function textResult(message) {
  assert.equal(message.error, undefined, JSON.stringify(message.error));
  assert.notEqual(message.result?.isError, true, JSON.stringify(message.result));
  return JSON.parse(message.result.content.find(item => item.type === 'text').text);
}

try {
  const initialized = await request('initialize', {
    protocolVersion: '2024-11-05',
    capabilities: {},
    clientInfo: { name: 'zuma-real-smoke', version: '1.0.0' }
  }).response;
  assert.ok(['Zuma', 'InazumaPwsh'].includes(initialized.result.serverInfo.name));
  send('notifications/initialized', {});

  const tools = await request('tools/list', {}).response;
  assert.equal(tools.result.tools.length, 18);
  assert.ok(tools.result.tools.some(tool => tool.name === 'execute_powershell'));
  assert.ok(tools.result.tools.some(tool => tool.name === 'run_affected_tests'));
  assert.ok(tools.result.tools.some(tool => tool.name === 'run_cargo_check'));

  const callTool = async (name, args, timeoutMs) => {
    const response = await request('tools/call', { name, arguments: args }, timeoutMs).response;
    return textResult(response);
  };
  const shell = (script, extra = {}) => callTool('execute_powershell', { script, ...extra });

  const peek = await callTool('peek_file', { path: path.join(root, 'README.md') });
  assert.equal(peek.status, 'success');
  assert.ok(peek.entries.length > 0);
  const usagePath = path.join(tmpdir(), 'inazuma-pwsh-logs', 'usage.jsonl');
  const usageRows = (await readFile(usagePath, 'utf8')).trim().split(/\r?\n/).map(line => JSON.parse(line));
  assert.ok(usageRows.some(row => row.tool === 'peek_file' && row.sourceBytes > 0 && row.responseBytes > 0));

  assert.equal((await shell('$global:zumaRustValue = 42', { sessionId: 'real-state' })).status, 'success');
  assert.equal((await shell('$global:zumaRustValue', { sessionId: 'real-state' })).data, 42);
  await Promise.all([
    shell('"ready"', { sessionId: 'real-parallel-1' }),
    shell('"ready"', { sessionId: 'real-parallel-2' })
  ]);
  const parallelStarted = performance.now();
  const parallel = await Promise.all([
    shell('Start-Sleep -Seconds 2; "one"', { sessionId: 'real-parallel-1' }),
    shell('Start-Sleep -Seconds 2; "two"', { sessionId: 'real-parallel-2' })
  ]);
  assert.deepEqual(parallel.map(result => result.data), ['one', 'two']);
  assert.ok(performance.now() - parallelStarted < 3500, 'sessions did not run concurrently');

  const marker = path.join(tmpdir(), `zuma-real-cancel-${process.pid}.txt`);
  const markerLiteral = `'${marker.replaceAll("'", "''")}'`;
  const cancellation = request('tools/call', {
    name: 'execute_powershell',
    arguments: {
      script: `[System.IO.File]::WriteAllText(${markerLiteral}, 'started'); Start-Sleep -Seconds 30`,
      sessionId: 'real-cancel',
      timeoutSeconds: 60
    }
  });
  const cancellationDeadline = Date.now() + 10000;
  while (true) {
    try { await stat(marker); break; } catch {
      assert.ok(Date.now() < cancellationDeadline, 'cancellation script did not start');
      await new Promise(resolve => setTimeout(resolve, 20));
    }
  }
  clearTimeout(pending.get(cancellation.id)?.timer);
  send('notifications/cancelled', { requestId: cancellation.id, reason: 'real smoke' });
  const resumedAt = performance.now();
  assert.equal((await shell('6 * 7', { sessionId: 'real-cancel' })).data, 42);
  assert.ok(performance.now() - resumedAt < 3000, 'cancellation did not release the real worker');
  pending.delete(cancellation.id);
  await rm(marker, { force: true });

  const buildProject = await mkdtemp(path.join(tmpdir(), 'zuma-real-build-'));
  const projectPath = path.join(buildProject, 'real-smoke.csproj');
  await writeFile(projectPath, '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>');
  await writeFile(path.join(buildProject, 'Program.cs'), 'using System;\nConsole.WriteLine("real smoke");\n');
  const build = await callTool('run_dotnet_build', {
    workingDirectory: buildProject,
    target: projectPath,
    includeWarnings: false,
    timeoutSeconds: 180
  }, 200000);
  await rm(buildProject, { recursive: true, force: true });
  assert.equal(build.status, 'success', JSON.stringify(build));
  console.log('REAL_SMOKE_OK tools=18 parallel=true cancellation=true dotnet_build=success');
} finally {
  child.stdin.end();
  child.kill();
}

const parent = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'ignore' });
const watched = spawn(executable, ['--parent-pid', String(parent.pid)], { stdio: ['pipe', 'pipe', 'pipe'] });
watched.stdin.write(`${JSON.stringify({
  jsonrpc: '2.0',
  id: 1,
  method: 'initialize',
  params: { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'parent-smoke', version: '1' } }
})}\n`);
await new Promise((resolve, reject) => {
  const timer = setTimeout(() => reject(new Error('parent-watched server did not initialize')), 10000);
  createInterface({ input: watched.stdout }).once('line', () => { clearTimeout(timer); resolve(); });
});
parent.kill();
await Promise.race([
  new Promise(resolve => watched.once('exit', resolve)),
  new Promise((_, reject) => setTimeout(() => reject(new Error('server did not exit after parent termination')), 5000))
]);
console.log('PARENT_LIFETIME_OK');
