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
const verificationHistoryPath = path.join(tmpdir(), `zuma-real-verification-${process.pid}.jsonl`);
const capsuleDirectory = path.join(tmpdir(), `zuma-real-capsules-${process.pid}`);

const child = spawn(executable, [], {
  cwd: root,
  stdio: ['pipe', 'pipe', 'pipe'],
  env: {
    ...process.env,
    ZUMA_WORKSPACE_ROOT: root,
    ZUMA_HANDOFF_ENABLED: 'true',
    ZUMA_VERIFICATION_HISTORY_PATH: verificationHistoryPath,
    ZUMA_CAPSULE_DIRECTORY: capsuleDirectory
  }
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

async function verifyTasks() {
  const taskChild = spawn(executable, [], {
    cwd: root,
    stdio: ['pipe', 'pipe', 'pipe'],
    env: { ...process.env, ZUMA_WORKSPACE_ROOT: root }
  });
  const taskPending = new Map();
  let taskNextId = 1;
  let taskStderr = '';
  taskChild.stderr.on('data', chunk => { taskStderr += chunk; });
  createInterface({ input: taskChild.stdout }).on('line', line => {
    const message = JSON.parse(line);
    const waiter = taskPending.get(message.id);
    if (!waiter) return;
    taskPending.delete(message.id);
    clearTimeout(waiter.timer);
    waiter.resolve(message);
  });
  const taskRequest = (method, params, timeoutMs = 120000) => {
    const id = taskNextId++;
    const response = new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`No task response for ${method}: ${taskStderr}`)), timeoutMs);
      taskPending.set(id, { resolve, reject, timer });
    });
    taskChild.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id, method, params })}\n`);
    return { id, response };
  };
  const taskMeta = {
    'io.modelcontextprotocol/protocolVersion': '2026-07-28',
    'io.modelcontextprotocol/clientCapabilities': {
      extensions: { 'io.modelcontextprotocol/tasks': {} }
    }
  };
  try {
    const anchorResult = (await taskRequest('tools/call', {
      name: 'set_task_anchor',
      arguments: {
        goal: 'Verify asynchronous execution',
        target: 'MCP task anchor propagation',
        acceptanceCriteria: ['Task responses contain the anchor'],
        outOfScope: ['Unrelated package work'],
        allowedPaths: [root],
        sessionId: 'task-anchor'
      }
    }).response).result;
    assert.equal(JSON.parse(anchorResult.content[0].text).status, 'set');
    const completedTask = (await taskRequest('tools/call', {
      name: 'execute_powershell',
      arguments: { script: 'Start-Sleep -Milliseconds 50; 42', sessionId: 'task-complete' },
      _meta: taskMeta
    }).response).result;
    assert.equal(completedTask.resultType, 'task', JSON.stringify(completedTask));
    assert.equal(completedTask.taskAnchor.target, 'MCP task anchor propagation');
    let completed;
    const completedDeadline = Date.now() + 10000;
    do {
      completed = (await taskRequest('tasks/get', { taskId: completedTask.taskId, _meta: taskMeta }).response).result;
      if (completed.status !== 'working') break;
      await new Promise(resolve => setTimeout(resolve, completed.pollIntervalMs ?? 250));
    } while (Date.now() < completedDeadline);
    assert.equal(completed.status, 'completed', JSON.stringify(completed));
    assert.equal(completed.taskAnchor.target, 'MCP task anchor propagation');
    assert.equal(textResult(completed).data, 42);

    const pendingTask = (await taskRequest('tools/call', {
      name: 'execute_powershell',
      arguments: { script: 'Start-Sleep -Seconds 10', sessionId: 'task-cancel' },
      _meta: taskMeta
    }).response).result;
    assert.equal(pendingTask.resultType, 'task');
    const taskCancellation = await taskRequest('tasks/cancel', {
      taskId: pendingTask.taskId,
      _meta: taskMeta
    }).response;
    assert.equal(taskCancellation.error, undefined, JSON.stringify(taskCancellation.error));
    let cancelled;
    const cancellationDeadline = Date.now() + 10000;
    do {
      cancelled = (await taskRequest('tasks/get', { taskId: pendingTask.taskId, _meta: taskMeta }).response).result;
      if (cancelled.status === 'cancelled') break;
      await new Promise(resolve => setTimeout(resolve, cancelled.pollIntervalMs ?? 250));
    } while (Date.now() < cancellationDeadline);
    assert.equal(cancelled.status, 'cancelled', JSON.stringify(cancelled));

    const unknownTaskRequest = taskRequest('tasks/get', { taskId: 'missing-task', _meta: taskMeta });
    const unknownTask = await unknownTaskRequest.response;
    assert.equal(unknownTask.id, unknownTaskRequest.id);
    assert.equal(unknownTask.error.code, -32602);
    assert.equal(unknownTask.result, undefined);
  } finally {
    taskChild.stdin.end();
    taskChild.kill();
  }
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
  assert.equal(tools.result.tools.length, 28);
  assert.ok(tools.result.tools.some(tool => tool.name === 'execute_powershell'));
  for (const name of ['set_task_anchor', 'get_task_anchor', 'clear_task_anchor', 'store_capsule', 'query_capsule'])
    assert.ok(tools.result.tools.some(tool => tool.name === name), `missing ${name}`);
  assert.ok(tools.result.tools.some(tool => tool.name === 'prepare_handoff'));
  assert.ok(tools.result.tools.some(tool => tool.name === 'run_affected_tests'));
  assert.ok(tools.result.tools.some(tool => tool.name === 'run_cargo_check'));
  assert.ok(tools.result.tools.some(tool => tool.name === 'read_symbol'));
  for (const name of ['get_execution_gate', 'set_execution_gate', 'reset_execution_gate'])
    assert.ok(tools.result.tools.some(tool => tool.name === name), `missing ${name}`);
  assert.deepEqual(initialized.result.capabilities.extensions['io.modelcontextprotocol/tasks'], {});
  await verifyTasks();

  const callTool = async (name, args, timeoutMs) => {
    const response = await request('tools/call', { name, arguments: args }, timeoutMs).response;
    return textResult(response);
  };
  const shell = (script, extra = {}) => callTool('execute_powershell', { script, ...extra });

  const budgetFixture = await mkdtemp(path.join(tmpdir(), 'zuma-real-budget-'));
  const largeLogPath = path.join(budgetFixture, 'large.log');
  await writeFile(largeLogPath, 'x'.repeat(137000));
  const baseCapsule = await callTool('store_capsule', {
    kind: 'log',
    sourcePath: largeLogPath,
    workingDirectory: budgetFixture,
    label: 'packaged-response-budget'
  });
  const storeResponseChars = JSON.stringify(baseCapsule).length;
  assert.ok(storeResponseChars < 137000 * 0.05, `store response used ${storeResponseChars} characters`);

  await writeFile(largeLogPath, `${'x'.repeat(137000)}\ndelta`);
  const updatedCapsule = await callTool('store_capsule', {
    kind: 'log',
    sourcePath: largeLogPath,
    workingDirectory: budgetFixture,
    label: 'packaged-response-budget'
  });
  const deltaCapsule = await callTool('query_capsule', {
    capsuleId: updatedCapsule.capsuleId,
    baseCapsuleId: baseCapsule.capsuleId,
    maxChars: 1000
  });
  const deltaResponseChars = JSON.stringify(deltaCapsule).length;
  assert.deepEqual(deltaCapsule.matches.map(match => match.text), ['delta']);
  assert.ok(deltaResponseChars < 137000 * 0.01, `delta response used ${deltaResponseChars} characters`);
  console.log(`PACKAGED_CAPSULE_RATIO ${((storeResponseChars / 137000) * 100).toFixed(3)}% DELTA_RATIO ${((deltaResponseChars / 137000) * 100).toFixed(3)}%`);
  await rm(budgetFixture, { recursive: true, force: true });

  const anchor = await callTool('set_task_anchor', {
    goal: 'Verify the packaged Zuma server',
    target: 'Task anchor and existing tools',
    acceptanceCriteria: ['Protocol and package smoke checks pass'],
    outOfScope: ['Marketplace publication'],
    allowedPaths: [root],
    sessionId: 'real-smoke'
  });
  assert.equal(anchor.status, 'set');
  const peek = await callTool('peek_file', { path: path.join(root, 'README.md') });
  assert.equal(peek.status, 'success');
  assert.equal(peek.taskAnchor.target, 'Task anchor and existing tools');
  assert.ok(peek.entries.length > 0);
  const codePeek = await callTool('peek_file', { path: path.join(root, 'FileReadingTools.cs') });
  assert.ok(codePeek.entries.some(entry => entry.includes('ReadSymbol')));
  const search = await callTool('search_files', {
    path: path.join(root, 'README.md'),
    pattern: 'read_symbol',
    literal: true,
    maxResults: 3
  });
  assert.ok(search.totalMatches > 0, JSON.stringify(search));
  assert.ok(search.files.some(file => file.file === 'README.md'));
  const smokeLog = path.join(tmpdir(), `zuma-real-log-${process.pid}.log`);
  await writeFile(smokeLog, '0 Error(s)\nreal smoke failure\nfinished');
  const summary = await callTool('summarize_log', { path: smokeLog, contextLines: 0, tailLines: 1 });
  await rm(smokeLog, { force: true });
  assert.equal(summary.matchedLines, 1, JSON.stringify(summary));
  assert.equal(summary.tail[0], '3: finished');
  const symbol = await callTool('read_symbol', {
    path: path.join(root, 'FileReadingTools.cs'),
    symbol: 'ReadSymbol'
  });
  assert.equal(symbol.status, 'success');
  assert.match(symbol.text, /ReadSymbol\(string path/);
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
  let resumed;
  while (true) {
    resumed = await shell('6 * 7', { sessionId: 'real-cancel' });
    if (resumed.data === 42) break;
    assert.ok(performance.now() - resumedAt < 3000, 'cancellation did not release the real worker');
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  assert.equal(resumed.data, 42);
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
  assert.equal(build.status, 'success', JSON.stringify(build));
  const verificationRows = (await readFile(verificationHistoryPath, 'utf8')).trim().split(/\r?\n/).map(line => JSON.parse(line));
  assert.ok(verificationRows.some(row => row.tool === 'run_dotnet_build' && row.status === 'success' && row.workspace === buildProject));
  const sessionPath = path.join(buildProject, 'synthetic-chat.jsonl');
  await writeFile(sessionPath, JSON.stringify({
    kind: 0,
    v: {
      version: 3,
      customTitle: 'Packaged smoke handoff',
      initialLocation: buildProject,
      requests: [{ message: { text: 'Verify the packaged Zuma handoff' }, response: [] }],
      inputState: { inputText: 'Continue with release verification' }
    }
  }));
  const handoff = await callTool('prepare_handoff', {
    sessionPath,
    workingDirectory: buildProject,
    nextActionHint: 'Continue with release verification',
    maxChars: 6000
  });
  assert.equal(handoff.status, 'success', JSON.stringify(handoff));
  assert.match(handoff.handoffText, /Verify the packaged Zuma handoff/);
  assert.match(handoff.handoffText, /run_dotnet_build: success/);
  assert.match(handoff.handoffText, /Continue with release verification/);
  const cleared = await callTool('clear_task_anchor', {});
  assert.equal(cleared.status, 'cleared');
  await rm(buildProject, { recursive: true, force: true });
  console.log('REAL_SMOKE_OK tools=28 anchor=true parallel=true cancellation=true dotnet_build=success handoff=success');
} finally {
  child.stdin.end();
  child.kill();
  await rm(verificationHistoryPath, { force: true });
  await rm(capsuleDirectory, { recursive: true, force: true });
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
