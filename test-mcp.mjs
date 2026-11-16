import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdir, mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));
const executable = process.argv[2] ?? path.join(root, 'bin/Debug/net10.0/win-x64/InazumaPwsh.exe');
const spawnStartedAt = performance.now();
const server = spawn(executable, process.argv.slice(3), {
    stdio: ['pipe', 'pipe', 'pipe'], env: { ...process.env, PSModulePath: '' }
});
const pending = new Map();
let nextId = 0;
let stderr = '';
let protocolError;
server.stderr.on('data', chunk => { stderr = (stderr + chunk).slice(-6000); });
server.on('error', error => { protocolError = error; });
createInterface({ input: server.stdout }).on('line', line => {
    try {
        const message = JSON.parse(line);
        assert.equal(message.jsonrpc, '2.0');
        if (pending.has(message.id)) {
            const entry = pending.get(message.id);
            pending.delete(message.id);
            clearTimeout(entry.timer);
            entry.resolve(message);
        }
    } catch (error) {
        protocolError = error;
    }
});

function send(method, params, id) {
    server.stdin.write(JSON.stringify({ jsonrpc: '2.0', ...(id === undefined ? {} : { id }), method, params }) + '\n');
}

function request(method, params) {
    const id = ++nextId;
    const response = new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
            pending.delete(id);
            reject(new Error(`No response for ${method}: ${protocolError ?? stderr}`));
        }, method === 'initialize' ? 120000 : 20000);
        pending.set(id, { resolve, reject, timer });
    });
    send(method, params, id);
    return { id, response };
}

function result(message) {
    assert.equal(message.error, undefined, JSON.stringify(message.error));
    assert.notEqual(message.result?.isError, true, JSON.stringify(message.result));
    return JSON.parse(message.result.content.find(item => item.type === 'text').text);
}

async function call(script, extra = {}) {
    return result(await request('tools/call', { name: 'execute_powershell', arguments: { script, ...extra } }).response);
}

const fixture = await mkdtemp(path.join(tmpdir(), 'inazuma-test-'));
const literal = value => `'${value.replaceAll("'", "''")}'`;
async function tool(name, args) {
    return result(await request('tools/call', { name, arguments: args }).response);
}
const startedAt = performance.now();
let passed = 0;
async function check(name, run) {
    await run();
    passed++;
    console.log(`PASS ${name}`);
}

try {
    const initialize = await request('initialize', {
        protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'inazuma-tests', version: '1.0.0' }
    }).response;
    assert.ok(initialize.result?.serverInfo);
    console.log(`MCP_INITIALIZE_MS ${(performance.now() - spawnStartedAt).toFixed(1)}`);
    send('notifications/initialized', {});
    const listed = await request('tools/list', {}).response;
    const execute = listed.result.tools.find(tool => tool.name === 'execute_powershell');
    assert.ok(execute);
    assert.equal(execute.inputSchema.properties.cancellationToken, undefined);
    await check('public tool descriptions are in English', async () => {
        for (const name of ['execute_powershell', 'reset_powershell_session', 'run_dotnet_build', 'run_playwright', 'inspect_playwright_trace', 'run_bun_test', 'run_pytest', 'run_vitest']) {
            const definition = listed.result.tools.find(tool => tool.name === name);
            assert.ok(definition?.description, `${name} is missing a description`);
            assert.match(definition.description, /^[\x00-\x7f]+$/, `${name} description is not English`);
            for (const [parameter, schema] of Object.entries(definition.inputSchema.properties ?? {})) {
                if (schema.description) assert.match(schema.description, /^[\x00-\x7f]+$/, `${name}.${parameter} description is not English`);
            }
        }
    });
    const warmTimings = [];
    for (let sample = 0; sample < 5; sample++) {
        const before = performance.now();
        assert.equal((await call('42')).data, 42);
        warmTimings.push(performance.now() - before);
    }
    warmTimings.sort((left, right) => left - right);
    console.log(`MCP_WARM_CALL_MEDIAN_MS ${warmTimings[2].toFixed(1)}`);
    await check('SDK rejects argument type mismatches', async () => {
        const invalid = await request('tools/call', { name: 'execute_powershell', arguments: { script: '42', timeoutSeconds: 'not-an-integer' } }).response;
        assert.ok(invalid.error || invalid.result?.isError, JSON.stringify(invalid));
        console.log(`SDK_ARGUMENT_ERROR ${invalid.error?.code ?? 'tool_isError'}`);
    });

    await check('standard commands without inherited module paths', async () => {
        for (const script of ['Get-Date', 'Get-ChildItem .', '42 | Select-Object -First 1', '42 | ConvertTo-Json']) {
            const response = await call(script, { workingDirectory: fixture });
            assert.equal(response.status, 'success', `${script}: ${JSON.stringify(response.errors)}`);
        }
    });
    if (spawnSync('python', ['--version'], { stdio: 'ignore' }).status === 0) await check('direct Python execution completes', async () => {
        const response = await call('python -c "print(123)"', { timeoutSeconds: 3, sessionId: 'python-test' });
        assert.equal(response.status, 'success', JSON.stringify(response));
        assert.equal(response.data, '123');
        const piped = await call("'hello' | python -c \"import sys; print(sys.stdin.read().strip())\"", { timeoutSeconds: 3, sessionId: 'python-test' });
        assert.equal(piped.status, 'success', JSON.stringify(piped));
        assert.equal(piped.data, 'hello');
        const failed = await call('python -c "import sys; sys.exit(7)"', { timeoutSeconds: 3, sessionId: 'python-test' });
        assert.equal(failed.status, 'error', JSON.stringify(failed));
        assert.equal(failed.exitCode, 7);
        const sameScriptFunction = await call('function python { $MyInvocation.ExpectingInput }; python', { sessionId: 'python-test' });
        assert.equal(sameScriptFunction.data, false, JSON.stringify(sameScriptFunction));
        const persistedFunction = await call('python', { sessionId: 'python-test' });
        assert.equal(persistedFunction.data, false, JSON.stringify(persistedFunction));
    });
    else console.log('SKIP direct Python execution: python is unavailable');
    await check('timeout returns promptly for blocking .NET calls', async () => {
        const startupBefore = performance.now();
        const startup = await call('42', { sessionId: 'blocking-test' });
        assert.equal(startup.status, 'success', JSON.stringify(startup));
        assert.equal(startup.data, 42);
        console.log(`MCP_NEW_SESSION_INITIALIZE_MS ${(performance.now() - startupBefore).toFixed(1)}`);
        const before = performance.now();
        const response = await call('$blockedValue = 42; [System.Threading.Thread]::Sleep(4000)', { timeoutSeconds: 1, sessionId: 'blocking-test' });
        assert.equal(response.status, 'timeout');
        assert.ok(performance.now() - before < 2500, 'timeout waited for the blocking call to finish');
        assert.ok(response.warnings.some(warning => warning.includes('may continue')));
        const busy = await call('$blockedValue = 99', { sessionId: 'blocking-test' });
        assert.equal(busy.status, 'error');
        assert.ok(busy.errors.some(error => error.includes('still stopping')));
        assert.equal((await call('42', { sessionId: 'other-test' })).data, 42);
        let resumed;
        const deadline = Date.now() + 6000;
        do {
            resumed = await call('$blockedValue', { sessionId: 'blocking-test' });
            if (resumed.status === 'success') break;
            assert.ok(Date.now() < deadline, 'blocking session did not recover');
            await new Promise(resolve => setTimeout(resolve, 100));
        } while (true);
        assert.equal(resumed.data, 42);
    });
    await check('persistent state and structured output', async () => {
        assert.equal((await call('$global:inazumaValue = 42; $inazumaValue')).data, 42);
        assert.equal((await call('$inazumaValue')).data, 42);
        assert.deepEqual((await call('[pscustomobject]@{ answer = 42; nested = @{ ok = $true } }')).data,
            { answer: 42, nested: { ok: true } });
    });
    await check('compact response preserves content and reduces duplication', async () => {
        const script = "'sample-' * 200";
        const regular = await call(script);
        const compact = await call(script, { compactResponse: true });
        assert.equal(compact.data, regular.data);
        assert.equal(compact.output, null);
        assert.ok(JSON.stringify(compact).length < JSON.stringify(regular).length * 0.6);
        const structured = await call('[pscustomobject]@{ answer = 42 }', { compactResponse: true });
        assert.deepEqual(structured.data, { answer: 42 });
        assert.equal(structured.output, null);
        const truncated = await call("'x' * 3000", { compactResponse: true, maxOutputChars: 1000 });
        assert.equal(truncated.isTruncated, true);
        assert.equal(truncated.data, null);
        assert.ok(truncated.output.length > 0);
        assert.ok(truncated.fullLogPath);
    });
    await check('syntax and information stream', async () => {
        assert.equal((await call('if (')).status, 'error');
        assert.equal((await call("Write-Host 'hello'" )).data, 'hello');
    });
    await check('timeout and subsequent reuse', async () => {
        assert.equal((await call('while ($true) {}', { timeoutSeconds: 1 })).status, 'timeout');
        assert.equal((await call('6 * 7')).data, 42);
    });
    await check('client cancellation never returns success', async () => {
        const marker = path.join(fixture, 'started');
        const work = request('tools/call', { name: 'execute_powershell', arguments: {
            script: `[System.IO.File]::WriteAllText(${literal(marker)}, 'started'); while ($true) {}`,
            timeoutSeconds: 10
        } });
        const deadline = Date.now() + 5000;
        while (true) {
            try { await stat(marker); break; } catch {
                assert.ok(Date.now() < deadline, 'script did not start');
                await new Promise(resolve => setTimeout(resolve, 10));
            }
        }
        let cancelled;
        void work.response.then(response => { cancelled = response; });
        clearTimeout(pending.get(work.id)?.timer);
        send('notifications/cancelled', { requestId: work.id, reason: 'test cancellation' });
        const resumeAt = performance.now();
        assert.equal((await call('6 * 7')).data, 42);
        assert.ok(performance.now() - resumeAt < 3000, 'cancellation did not release execution');
        if (cancelled?.error) assert.equal(cancelled.error.code, -32800);
        else if (cancelled) assert.equal(result(cancelled).status, 'cancelled');
        pending.delete(work.id);
    });
    await check('explicit working directory and invalid path', async () => {
        const before = (await call('(Get-Location).Path')).data;
        assert.equal(path.resolve((await call('(Get-Location).Path', { workingDirectory: fixture })).data), path.resolve(fixture));
        assert.equal((await call('(Get-Location).Path')).data, before);
        const invalid = await call("'must not execute'", { workingDirectory: path.join(fixture, 'missing') });
        assert.equal(invalid.status, 'error');
        assert.notEqual(invalid.data, 'must not execute');
    });
    await check('strict output cap, ANSI removal and full log', async () => {
        const compressed = await call("[char]27 + '[31m' + ('x' * 10000) + [char]27 + '[0m'; 'error CS1002: expected semicolon'", { maxOutputChars: 1000 });
        assert.equal(compressed.isTruncated, true);
        assert.ok(compressed.output.length <= 1000);
        assert.ok(!compressed.output.includes('\x1b'));
        assert.ok(compressed.output.includes('CS1002'));
        assert.ok((await readFile(compressed.fullLogPath, 'utf8')).includes('x'.repeat(10000)));
        await rm(compressed.fullLogPath);
        assert.deepEqual((await call('[pscustomobject]@{ answer = 42 }', { maxOutputChars: 1000 })).data, { answer: 42 });
    });
    await check('nonzero native exit and stream errors', async () => {
        assert.notEqual((await call("cmd /c 'exit 7'")).status, 'success');
        assert.notEqual((await call("Write-Error 'broken'; 'partial'")).status, 'success');
        assert.equal((await call('6 * 7')).status, 'success');
    });
    await check('reset', async () => {
        result(await request('tools/call', { name: 'reset_powershell_session', arguments: {} }).response);
        assert.equal((await call('$inazumaValue')).data, null);
    });
    await check('parallel protocol integrity', async () => {
        const responses = await Promise.all(Array.from({ length: 12 }, (_, index) => call(String(index))));
        assert.deepEqual(responses.map(response => response.data), Array.from({ length: 12 }, (_, index) => index));
        assert.equal(protocolError, undefined);
    });
    await check('isolated chat sessions execute concurrently', async () => {
        await call('$global:chatValue = 11', { sessionId: 'chat-one' });
        await call('$global:chatValue = 22', { sessionId: 'chat-two' });
        assert.equal((await call('$chatValue', { sessionId: 'chat-one' })).data, 11);
        assert.equal((await call('$chatValue', { sessionId: 'chat-two' })).data, 22);
        const parallelAt = performance.now();
        const values = await Promise.all(['chat-one', 'chat-two'].map(sessionId =>
            call('Start-Sleep -Milliseconds 1500; $chatValue', { sessionId })));
        assert.deepEqual(values.map(value => value.data), [11, 22]);
        assert.ok(performance.now() - parallelAt < 2700, 'different chats executed serially');
        result(await request('tools/call', { name: 'reset_powershell_session', arguments: { sessionId: 'chat-one' } }).response);
        assert.equal((await call('$chatValue', { sessionId: 'chat-one' })).data, null);
        assert.equal((await call('$chatValue', { sessionId: 'chat-two' })).data, 22);
    });
    await check('structured build failure and successful rebuild', async () => {
        const project = path.join(fixture, 'build');
        await mkdir(project);
        await writeFile(path.join(project, 'Probe.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup></Project>');
        await writeFile(path.join(project, 'Program.cs'), 'MissingThing();');
        const failed = await tool('run_dotnet_build', { workingDirectory: project });
        assert.equal(failed.status, 'error', JSON.stringify(failed));
        assert.notEqual(failed.exitCode, 0);
        assert.ok(failed.errors.some(error => error.ruleId === 'CS0103'), JSON.stringify(failed));
        assert.ok((await stat(failed.fullLogPath)).size > 0);
        await writeFile(path.join(project, 'Program.cs'), 'System.Console.WriteLine("ok");');
        const success = await tool('run_dotnet_build', { workingDirectory: project });
        assert.equal(success.status, 'success', JSON.stringify(success));
        await rm(failed.fullLogPath);
        await rm(success.fullLogPath);
    });
    await check('Playwright global error and nonzero exit never succeed', async () => {
        const project = path.join(fixture, 'playwright');
        const cliDirectory = path.join(project, 'node_modules', 'playwright');
        await mkdir(cliDirectory, { recursive: true });
        await writeFile(path.join(cliDirectory, 'cli.js'), `const fs=require('node:fs'); fs.writeFileSync(process.env.PLAYWRIGHT_JSON_OUTPUT_NAME,JSON.stringify({errors:[{message:'fixture launch failure'}],suites:[],stats:{duration:2}})); console.error('fixture failure'); process.exitCode=2;`);
        const failure = await tool('run_playwright', { workingDirectory: project });
        assert.equal(failure.status, 'error', JSON.stringify(failure));
        assert.equal(failure.exitCode, 2);
        assert.ok(failure.globalErrors.includes('fixture launch failure'));
        await rm(failure.fullLogPath);
    });
    await check('Playwright malformed reports never false-success', async () => {
        const project = path.join(fixture, 'playwright-malformed');
        const cliDirectory = path.join(project, 'node_modules', 'playwright');
        await mkdir(cliDirectory, { recursive: true });
        const cliPath = path.join(cliDirectory, 'cli.js');

        // 1. root not-object
        await writeFile(cliPath, `const fs=require('node:fs'); fs.writeFileSync(process.env.PLAYWRIGHT_JSON_OUTPUT_NAME, JSON.stringify(['not-an-object'])); process.exitCode=0;`);
        const notObject = await tool('run_playwright', { workingDirectory: project });
        assert.equal(notObject.status, 'report_unavailable', JSON.stringify(notObject));
        assert.equal(notObject.exitCode, 0);
        await rm(notObject.fullLogPath);

        await writeFile(cliPath, `const fs=require('node:fs'); fs.writeFileSync(process.env.PLAYWRIGHT_JSON_OUTPUT_NAME, JSON.stringify({ stats: { duration: 1 } })); process.exitCode=0;`);
        const missingSchema = await tool('run_playwright', { workingDirectory: project });
        assert.equal(missingSchema.status, 'report_unavailable', JSON.stringify(missingSchema));
        assert.equal(missingSchema.exitCode, 0);
        await rm(missingSchema.fullLogPath);

        await writeFile(cliPath, `const fs=require('node:fs'); fs.writeFileSync(process.env.PLAYWRIGHT_JSON_OUTPUT_NAME, JSON.stringify({ suites: 'invalid', stats: { duration: 1 } })); process.exitCode=0;`);
        const suitesString = await tool('run_playwright', { workingDirectory: project });
        assert.equal(suitesString.status, 'report_unavailable', JSON.stringify(suitesString));
        assert.equal(suitesString.exitCode, 0);
        await rm(suitesString.fullLogPath);

        await writeFile(cliPath, `const fs=require('node:fs'); fs.writeFileSync(process.env.PLAYWRIGHT_JSON_OUTPUT_NAME, JSON.stringify({ suites: {}, stats: { duration: 1 } })); process.exitCode=0;`);
        const suitesObject = await tool('run_playwright', { workingDirectory: project });
        assert.equal(suitesObject.status, 'report_unavailable', JSON.stringify(suitesObject));
        assert.equal(suitesObject.exitCode, 0);
        await rm(suitesObject.fullLogPath);
    });
    await check('Playwright giant failure title, stack and attachments bounded under 8000 chars', async () => {
        const project = path.join(fixture, 'playwright-giant');
        const cliDirectory = path.join(project, 'node_modules', 'playwright');
        await mkdir(cliDirectory, { recursive: true });
        const cliPath = path.join(cliDirectory, 'cli.js');

        const giantScript = `
const fs = require('node:fs');
const giantTitle = 'T'.repeat(8000);
const giantPath = 'P'.repeat(4000);
const giantStack = Array.from({ length: 80 }, (_, i) => '    at step' + i + ' (' + 'S'.repeat(400) + ':10:20)').join('\\n');
const giantMsg = 'M'.repeat(8000);
const payload = {
    stats: { duration: 12.5, expected: 0, unexpected: 1, flaky: 0, skipped: 0 },
    suites: [{
        title: 'Suite_' + 'X'.repeat(500),
        file: giantPath,
        specs: [{
            title: giantTitle,
            file: giantPath,
            tests: [{
                status: 'unexpected',
                expectedStatus: 'passed',
                projectName: 'Proj_' + 'Y'.repeat(200),
                results: [{
                    status: 'timedOut',
                    errors: [{ message: giantMsg, stack: giantStack }],
                    attachments: [
                        { name: 'attach1_' + 'A'.repeat(500), path: giantPath + '/shot.png', contentType: 'image/png' },
                        { name: 'attach2_' + 'B'.repeat(500), path: giantPath + '/trace.zip', contentType: 'application/zip' }
                    ]
                }]
            }]
        }]
    }]
};
fs.writeFileSync(process.env.PLAYWRIGHT_JSON_OUTPUT_NAME, JSON.stringify(payload));
process.stderr.write('Giant test stderr: ' + 'E'.repeat(3000));
process.exitCode = 1;
`;
        await writeFile(cliPath, giantScript);
        const giant = await tool('run_playwright', { workingDirectory: project });
        const jsonLength = JSON.stringify(giant).length;
        assert.ok(jsonLength <= 8000, `Expected total JSON length <= 8000, got ${jsonLength}`);
        assert.equal(giant.status, 'error', JSON.stringify(giant));
        assert.equal(giant.exitCode, 1);
        assert.ok(giant.failures?.length > 0, 'failures must contain primary diagnosis');
        assert.ok(giant.fullLogPath, 'fullLogPath must exist');
        if (giant.fullReportPath) {
            assert.ok((await stat(giant.fullReportPath)).size > 0);
            await rm(giant.fullReportPath);
        }
        await rm(giant.fullLogPath);
    });
    await check('Vitest JSON failures, file errors and managed reporter', async () => {
        const project = path.join(fixture, 'vitest');
        const cliDirectory = path.join(project, 'node_modules', 'vitest');
        await mkdir(cliDirectory, { recursive: true });
        const cliPath = path.join(cliDirectory, 'vitest.mjs');
        const spec = path.join(project, 'math.spec.ts').replaceAll('\\', '/');
        const writer = payload => `import fs from 'node:fs'; const out = process.argv.find(a => a.startsWith('--outputFile=')).slice(13); if (process.argv[2] !== 'run') process.exit(9); fs.writeFileSync(out, JSON.stringify(${JSON.stringify(payload)})); process.exitCode = ${payload.success ? 0 : 1};`;
        await writeFile(cliPath, writer({
            success: false, startTime: 1000, testResults: [
                { name: spec, status: 'failed', message: '', endTime: 1250, assertionResults: [
                    { fullName: 'math adds', status: 'passed', failureMessages: [] },
                    { fullName: 'math fails', status: 'failed', failureMessages: [`AssertionError: expected 1 to be 2\n    at ${spec}:7:13\n    at new Promise (<anonymous>)\n    at file:///${project.replaceAll('\\', '/')}/node_modules/vitest/dist/run.js:1:1`] },
                    { fullName: 'math later', status: 'skipped', failureMessages: [] },
                    { fullName: 'math todo', status: 'todo', failureMessages: [] }
                ] },
                { name: path.join(project, 'broken.spec.ts'), status: 'failed', message: "Cannot find module './missing'", assertionResults: [] }
            ]
        }));
        const failed = await tool('run_vitest', { workingDirectory: project });
        assert.equal(failed.status, 'error', JSON.stringify(failed));
        assert.deepEqual([failed.stats.passed, failed.stats.failed, failed.stats.skipped, failed.stats.total], [1, 1, 2, 4]);
        assert.equal(failed.stats.duration, 250);
        assert.equal(failed.failures[0].title, 'math fails');
        assert.equal(failed.failures[0].file, 'math.spec.ts');
        assert.equal(failed.failures[0].line, 7);
        assert.equal(failed.failures[0].errors[0].message, 'AssertionError: expected 1 to be 2');
        assert.equal(failed.failures[0].errors[0].stack, `at ${spec}:7:13`);
        assert.ok(failed.globalErrors.some(error => error.startsWith('broken.spec.ts: Cannot find module')), JSON.stringify(failed));
        await rm(failed.fullLogPath);

        await writeFile(cliPath, writer({ success: true, testResults: [{ name: spec, status: 'passed', message: '', assertionResults: [{ fullName: 'ok', status: 'passed', failureMessages: [] }] }] }));
        const success = await tool('run_vitest', { workingDirectory: project, testArgs: ['math.spec.ts'] });
        assert.equal(success.status, 'success', JSON.stringify(success));
        assert.equal(success.output, undefined);
        await rm(success.fullLogPath);

        await writeFile(cliPath, writer({ success: true, testResults: 'invalid' }));
        const malformed = await tool('run_vitest', { workingDirectory: project });
        assert.equal(malformed.status, 'report_unavailable', JSON.stringify(malformed));
        await rm(malformed.fullLogPath);

        const rejected = await tool('run_vitest', { workingDirectory: project, testArgs: ['--reporter=verbose'] });
        assert.equal(rejected.status, 'error');
        assert.match(rejected.message, /managed by this tool/);
        assert.equal((await tool('run_vitest', { workingDirectory: fixture })).status, 'error');
    });
    await check('pytest JUnit XML parsing with a fake interpreter', async () => {
        const project = path.join(fixture, 'pytest');
        await mkdir(project);
        const fakePython = path.join(project, 'fake-python.cmd');
        await writeFile(path.join(project, 'fake-pytest.mjs'), `import fs from 'node:fs'; const out = process.argv.find(a => a.startsWith('--junitxml=')).slice(11); if (process.argv[2] !== '-m' || process.argv[3] !== 'pytest') process.exit(9); const mode = fs.readFileSync('mode.txt', 'utf8').trim(); const payloads = JSON.parse(fs.readFileSync('payloads.json', 'utf8')); fs.writeFileSync(out, payloads[mode].xml); process.exitCode = payloads[mode].exitCode;`);
        await writeFile(fakePython, '@node "%~dp0fake-pytest.mjs" %*\r\n');
        await writeFile(path.join(project, 'payloads.json'), JSON.stringify({
            failed: { exitCode: 1, xml: `<?xml version="1.0" encoding="utf-8"?><testsuites name="pytest tests"><testsuite name="pytest" errors="1" failures="1" skipped="1" tests="4" time="0.5"><testcase classname="test_sample" name="test_passes" time="0.001" /><testcase classname="test_sample" name="test_fails" time="0.001"><failure message="AssertionError: assert 1 == 2">def test_fails():\n&gt;       assert 1 == 2\nE       AssertionError: assert 1 == 2\n\ntest_sample.py:9: AssertionError</failure></testcase><testcase classname="test_sample" name="test_skipped" time="0"><skipped type="pytest.skip" message="demo">test_sample.py:12: demo</skipped></testcase><testcase classname="test_sample" name="test_setup" time="0"><error message="failed on setup">file ${project}\\test_sample.py, line 17\n  def test_setup(missing_fixture):\nE       fixture 'missing_fixture' not found</error></testcase></testsuite></testsuites>` },
            empty: { exitCode: 5, xml: '<?xml version="1.0" encoding="utf-8"?><testsuites name="pytest tests"><testsuite name="pytest" errors="0" failures="0" skipped="0" tests="0" time="0.01" /></testsuites>' },
            dtd: { exitCode: 0, xml: '<?xml version="1.0"?><!DOCTYPE x [<!ENTITY a "aaaaaaaaaa">]><testsuites><testsuite><testcase name="&a;" /></testsuite></testsuites>' }
        }));
        await writeFile(path.join(project, 'mode.txt'), 'failed');
        const failed = await tool('run_pytest', { workingDirectory: project, pythonPath: fakePython });
        assert.equal(failed.status, 'error', JSON.stringify(failed));
        assert.deepEqual([failed.stats.passed, failed.stats.failed, failed.stats.skipped, failed.stats.total], [1, 2, 1, 4]);
        assert.equal(failed.stats.duration, 500);
        assert.deepEqual(failed.failures.map(failure => [failure.title, failure.file, failure.line, failure.status]),
            [['test_sample > test_fails', 'test_sample.py', 9, 'failure'], ['test_sample > test_setup', 'test_sample.py', 17, 'error']]);
        assert.equal(failed.failures[0].errors[0].message, 'AssertionError: assert 1 == 2');
        await rm(failed.fullLogPath);

        await writeFile(path.join(project, 'mode.txt'), 'empty');
        const empty = await tool('run_pytest', { workingDirectory: project, pythonPath: fakePython });
        assert.equal(empty.status, 'no_tests', JSON.stringify(empty));
        await rm(empty.fullLogPath);

        await writeFile(path.join(project, 'mode.txt'), 'dtd');
        const dtd = await tool('run_pytest', { workingDirectory: project, pythonPath: fakePython });
        assert.equal(dtd.status, 'report_unavailable', JSON.stringify(dtd));
        await rm(dtd.fullLogPath);

        const rejected = await tool('run_pytest', { workingDirectory: project, testArgs: ['--junitxml=other.xml'] });
        assert.match(rejected.message, /managed by this tool/);
        const relative = await tool('run_pytest', { workingDirectory: project, pythonPath: 'python.exe' });
        assert.match(relative.message, /pythonPath must be/);
    });
    if (process.env.INAZUMA_TEST_PYTHON) {
        await check('real pytest JUnit report', async () => {
            const project = path.join(fixture, 'pytest-real');
            await mkdir(project);
            await writeFile(path.join(project, 'test_real.py'), 'def test_ok():\n    assert True\n\n\ndef test_bad():\n    assert 1 == 2\n');
            const real = await tool('run_pytest', { workingDirectory: project, pythonPath: process.env.INAZUMA_TEST_PYTHON, testArgs: ['-p', 'no:cacheprovider'] });
            assert.equal(real.status, 'error', JSON.stringify(real));
            assert.deepEqual([real.stats.passed, real.stats.failed], [1, 1]);
            assert.equal(real.failures[0].file, 'test_real.py');
            assert.equal(real.failures[0].line, 6);
            await rm(real.fullLogPath);
        });
    }
    if (spawnSync('bun', ['--version'], { stdio: 'ignore' }).status === 0) {
        await check('real bun test JUnit report', async () => {
            const project = path.join(fixture, 'bun');
            await mkdir(project);
            await writeFile(path.join(project, 'sample.test.ts'), "import { describe, expect, test } from 'bun:test';\ndescribe('group', () => {\n    test('passes', () => { expect(1).toBe(1); });\n    test('fails', () => { expect(1).toBe(2); });\n    test.skip('skipped', () => {});\n});\n");
            const failed = await tool('run_bun_test', { workingDirectory: project });
            assert.equal(failed.status, 'error', JSON.stringify(failed));
            assert.deepEqual([failed.stats.passed, failed.stats.failed, failed.stats.skipped], [1, 1, 1]);
            assert.deepEqual([failed.failures[0].title, failed.failures[0].file, failed.failures[0].line], ['group > fails', 'sample.test.ts', 4]);
            await rm(failed.fullLogPath);
            await writeFile(path.join(project, 'sample.test.ts'), "import { expect, test } from 'bun:test';\ntest('passes', () => { expect(1).toBe(1); });\n");
            const success = await tool('run_bun_test', { workingDirectory: project });
            assert.equal(success.status, 'success', JSON.stringify(success));
            await rm(success.fullLogPath);
            assert.match((await tool('run_bun_test', { workingDirectory: project, testArgs: ['--reporter=dots'] })).message, /managed by this tool/);
        });
    }
    await check('run_dotnet_build with structurally incomplete SARIF yields report_unavailable', async () => {
        const project = path.join(fixture, 'build-incomplete');
        await mkdir(project);
        const csproj = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <Target Name="CorruptSarif" AfterTargets="Build">
    <ItemGroup>
      <_Sarifs Include="$([System.IO.Path]::GetDirectoryName('$(CustomAfterMicrosoftCommonTargets)'))\\*.sarif" />
    </ItemGroup>
    <Delete Files="@(_Sarifs)" />
    <WriteLinesToFile File="$([System.IO.Path]::GetDirectoryName('$(CustomAfterMicrosoftCommonTargets)'))\\incomplete.sarif" Lines="{&quot;version&quot;:&quot;2.1.0&quot;,&quot;runs&quot;:[]}" Overwrite="true" />
  </Target>
</Project>`;
        await writeFile(path.join(project, 'Incomplete.csproj'), csproj);
        await writeFile(path.join(project, 'Program.cs'), 'System.Console.WriteLine("ok");');
        const incomplete = await tool('run_dotnet_build', { workingDirectory: project });
        assert.equal(incomplete.status, 'report_unavailable', JSON.stringify(incomplete));
        assert.equal(incomplete.exitCode, 0);
        await rm(incomplete.fullLogPath);

        const csprojMalformed = csproj.replace(
            '{&quot;version&quot;:&quot;2.1.0&quot;,&quot;runs&quot;:[]}',
            '{ &quot;runs&quot;: [ unclosed'
        );
        await writeFile(path.join(project, 'Incomplete.csproj'), csprojMalformed);
        const malformed = await tool('run_dotnet_build', { workingDirectory: project });
        assert.equal(malformed.status, 'report_unavailable', JSON.stringify(malformed));
        assert.equal(malformed.exitCode, 0);
        await rm(malformed.fullLogPath);
    });
    await check('native timeout kills spawned children', async () => {
        const project = path.join(fixture, 'timeout');
        const cliDirectory = path.join(project, 'node_modules', 'playwright');
        const marker = path.join(project, 'child-finished');
        await mkdir(cliDirectory, { recursive: true });
        await writeFile(path.join(cliDirectory, 'cli.js'), `require('node:child_process').spawn(process.execPath,['-e',${JSON.stringify(`setTimeout(()=>require('node:fs').writeFileSync(${JSON.stringify(marker)},'leaked'),2500)`)}],{stdio:'ignore'}); setInterval(()=>{},1000);`);
        const timeout = await tool('run_playwright', { workingDirectory: project, timeoutSeconds: 1 });
        assert.equal(timeout.status, 'timeout', JSON.stringify(timeout));
        await call('Start-Sleep -Milliseconds 2000');
        await assert.rejects(stat(marker), { code: 'ENOENT' });
        await rm(timeout.fullLogPath);
    });
    await check('trace failure, HTTP resource and pre-failure DOM', async () => {
        const zipPath = path.join(fixture, 'trace.zip');
        const trace = [
            { type: 'before', callId: 'call1', apiName: 'page.click', startTime: 10, params: { selector: '#submit' } },
            { type: 'frame-snapshot', snapshot: { timestamp: 8, isMainFrame: true, frameId: 'frame1', html: ['HTML', {}, ['HEAD', {}, ['BASE', { href: 'about:blank' }]], ['BODY', {}, ['BUTTON', {}, 'Send']]] } },
            { type: 'frame-snapshot', snapshot: { timestamp: 9, isMainFrame: true, frameId: 'frame1', html: ['HTML', {}, ['HEAD', {}, [[1, 0]]], ['BODY', {}, ['BUTTON', { id: 'submit' }, [[1, 2]]]]] } },
            { type: 'frame-snapshot', snapshot: { callId: 'call1', snapshotName: 'before@call1', timestamp: 9.5, isMainFrame: true, frameId: 'frame1', html: ['HTML', {}, ['BODY', {}, [[1, 1]], ['SCRIPT', {}, 'noisy']]] } },
            { type: 'error', message: 'Fixture exception', stack: [{ file: 'test.js', line: 8, column: 3 }] },
            { type: 'after', callId: 'call1', endTime: 20, error: { message: 'Timeout waiting for submit' } },
            { type: 'frame-snapshot', snapshot: { timestamp: 30, isMainFrame: true, frameId: 'frame1', html: ['BODY', {}, 'AFTER_FAILURE'] } }
        ].map(value => JSON.stringify(value)).join('\n');
        const network = JSON.stringify({ type: 'resource-snapshot', snapshot: { request: { method: 'GET', url: 'https://example.test/api?token=hidden' }, response: { status: 500, content: { mimeType: 'application/json', _sha1: 'body' } } } });
        await call(`Add-Type -AssemblyName System.IO.Compression; $z = [System.IO.Compression.ZipArchive]::new([System.IO.File]::Create(${literal(zipPath)}), [System.IO.Compression.ZipArchiveMode]::Create); try { foreach ($pair in @(@('0.trace',${literal(trace)}),@('0.network',${literal(network)}),@('resources/body','server error'))) { $writer = [System.IO.StreamWriter]::new($z.CreateEntry($pair[0]).Open()); $writer.Write($pair[1]); $writer.Dispose() } } finally { $z.Dispose() }`);
        const inspected = await tool('inspect_playwright_trace', { tracePath: zipPath, maxOutputChars: 2000 });
        assert.equal(inspected.failedAction.selector, '#submit', JSON.stringify(inspected));
        assert.ok(inspected.httpErrors.some(error => error.status === 500));
        assert.ok(!JSON.stringify(inspected).includes('hidden'));
        assert.ok(!JSON.stringify(inspected).includes('AFTER_FAILURE'));
        assert.ok(!JSON.stringify(inspected).includes('noisy'));
        assert.ok(inspected.latestDomSnapshot.includes('Send'), JSON.stringify(inspected));
        assert.ok(!inspected.warnings.some(warning => warning.includes('Error parsing line')), JSON.stringify(inspected));
        assert.ok(JSON.stringify(inspected).length <= 2000);
        assert.equal((await tool('inspect_playwright_trace', { tracePath: path.join(fixture, 'missing.zip') })).status, 'error');
    });
    if (process.env.INAZUMA_REAL_TRACE) {
        await check('real Chrome trace error and DOM', async () => {
            const inspected = await tool('inspect_playwright_trace', { tracePath: process.env.INAZUMA_REAL_TRACE, maxOutputChars: 5000 });
            assert.equal(inspected.status, 'inspected', JSON.stringify(inspected));
            assert.ok(inspected.latestDomSnapshot?.includes('Actual result'), JSON.stringify(inspected));
            assert.ok(JSON.stringify(inspected).includes('fixture console error'), JSON.stringify(inspected));
        });
    }
    await check('parent process lifetime', async () => {
        const parent = spawn(process.execPath, ['-e', 'setInterval(()=>{},1000)'], { stdio: 'ignore' });
        const child = spawn(executable, ['--parent-pid', String(parent.pid)], { stdio: ['pipe', 'pipe', 'ignore'] });
        let timer;
        const exited = new Promise((resolve, reject) => {
            timer = setTimeout(() => reject(new Error('parent exit did not stop server')), 10000);
            child.once('exit', code => { clearTimeout(timer); resolve(code); });
        });
        try {
            const ready = new Promise(resolve => createInterface({ input: child.stdout }).once('line', resolve));
            child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: {
                protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'parent-test', version: '1' }
            } }) + '\n');
            await Promise.race([ready, exited.then(() => { throw new Error('server exited before initialize'); })]);
            parent.kill();
            assert.equal(await exited, 0);
        } finally {
            clearTimeout(timer);
            parent.kill();
            child.kill();
        }
    });
    console.log(`MCP_TESTS_PASSED ${passed} ${(performance.now() - startedAt).toFixed(0)}ms`);
} finally {
    for (const entry of pending.values()) clearTimeout(entry.timer);
    server.stdin.end();
    if (server.exitCode === null) server.kill();
    await rm(fixture, { recursive: true, force: true });
}