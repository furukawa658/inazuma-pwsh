import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { lstat, mkdir, mkdtemp, readFile, realpath, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';

const root = path.dirname(fileURLToPath(import.meta.url));
const hostRid = process.platform === 'win32' ? 'win-x64'
  : process.platform === 'darwin' ? (process.arch === 'arm64' ? 'osx-arm64' : 'osx-x64')
  : (process.arch === 'arm64' ? 'linux-arm64' : 'linux-x64');
const executable = process.argv[2] ?? path.join(root, 'bin/Debug/net10.0', hostRid, process.platform === 'win32' ? 'InazumaPwsh.exe' : 'InazumaPwsh');
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

async function rawCall(script, extra = {}) {
    const message = await request('tools/call', { name: 'execute_powershell', arguments: { script, ...extra } }).response;
    assert.equal(message.error, undefined, JSON.stringify(message.error));
    assert.notEqual(message.result?.isError, true, `${JSON.stringify(message.result)}\n${stderr}`);
    return message.result.content.find(item => item.type === 'text').text;
}

const fixture = await mkdtemp(path.join(tmpdir(), 'inazuma-test-'));
const literal = value => `'${value.replaceAll("'", "''")}'`;
async function tool(name, args) {
    return result(await request('tools/call', { name, arguments: args }).response);
}
const startedAt = performance.now();
let passed = 0;
async function check(name, run) {
    if (process.env.INAZUMA_OUTLINE_ONLY && !name.startsWith('outline regression')) return;
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
        for (const name of ['execute_powershell', 'reset_powershell_session', 'run_dotnet_build', 'run_dotnet_test', 'run_playwright', 'inspect_playwright_trace', 'run_bun_test', 'run_pytest', 'run_vitest', 'run_tsc', 'run_eslint', 'peek_file', 'read_section', 'summarize_log', 'search_files']) {
            const definition = listed.result.tools.find(tool => tool.name === name);
            assert.ok(definition?.description, `${name} is missing a description`);
            assert.match(definition.description, /^[\x00-\x7f]+$/, `${name} description is not English`);
            for (const [parameter, schema] of Object.entries(definition.inputSchema.properties ?? {})) {
                if (schema.description) assert.match(schema.description, /^[\x00-\x7f]+$/, `${name}.${parameter} description is not English`);
            }
        }
    });
    await check('responses do not escape quotes or Japanese', async () => {
        const raw = await rawCall(`'say "hi" 日本語'`, { compactResponse: false });
        assert.ok(!/\\u[0-9a-fA-F]{4}/.test(raw), raw);
        assert.ok(raw.includes('日本語'), raw);
    });
    await check('compactResponse defaults to true', async () => {
        const response = await call('42');
        assert.equal(response.data, 42);
        assert.equal(response.output, undefined);
    });
    await check('native UTF-8 output is decoded', async () => {
        const response = await call(`& ${literal(process.execPath)} -e "process.stdout.write(Buffer.from('e697a5e69cace8aa9e','hex'))"`);
        assert.equal(response.data, '日本語', JSON.stringify(response));
    });
    await check('redirected native stderr stays plain text', async () => {
        const raw = await rawCall(`& ${literal(process.execPath)} -e "console.log('out');console.error('err-line')" 2>&1`);
        const response = JSON.parse(raw);
        assert.deepEqual(response.data, ['out', 'err-line'], raw);
        assert.ok(raw.length < 1000, `response too large: ${raw.length}`);
    });
    await check('outline regression handles long markdown fences', async () => {
        const docPath = path.join(fixture, 'fences.md');
        await writeFile(docPath, ['# First', '````markdown', '```', '# Hidden', '````', '## Second',
            '~~~', '~~~not-a-close', '# Also hidden', '~~~', '## Third'].join('\n'));
        const result = await tool('peek_file', { path: docPath });
        assert.deepEqual(result.entries.map(entry => entry.title), ['First', 'Second', 'Third']);
        assert.equal(result.entries[1].end, 10);
    });
    await check('outline regression parses PowerShell declarations and ranges', async () => {
        const scriptPath = path.join(fixture, 'script.ps1');
        await writeFile(scriptPath, [
            'function Outer {', "@'", 'function Ghost {}', "'@", '<#', 'function Commented {}', '#>',
            'function Inner {', "  '}'", '}', '}', "function After { 'ok' }"
        ].join('\n'));
        const result = await tool('peek_file', { path: scriptPath });
        assert.deepEqual(result.entries.map(entry => [entry.title, entry.start, entry.end, entry.level]), [
            ['function Outer {', 1, 11, 1], ['function Inner {', 8, 10, 2], ["function After { 'ok' }", 12, 12, 1]
        ]);
        const section = await tool('read_section', { path: scriptPath, query: 'Outer' });
        assert.equal(section.endLine, 11);
    });
    await check('outline regression does not execute PowerShell source', async () => {
        const scriptPath = path.join(fixture, 'module.psm1');
        const markerPath = path.join(fixture, 'must-not-exist');
        await writeFile(scriptPath, [`Set-Content -Path ${literal(markerPath)} -Value 'executed'`,
            'class Widget {', '    [void] Run() {', '    }', '}'].join('\n'));
        const result = await tool('peek_file', { path: scriptPath });
        assert.deepEqual(result.entries.map(entry => [entry.start, entry.end, entry.level]), [[2, 5, 1], [3, 4, 2]]);
        await assert.rejects(stat(markerPath), { code: 'ENOENT' });
    });
    await check('peek_file outlines markdown and recommends partial reads', async () => {
        const sections = [];
        for (let i = 1; i <= 30; i++) {
            sections.push(`## Section ${i}`);
            for (let j = 1; j <= 10; j++) {
                if (i === 3 && j === 5) {
                    sections.push('```');
                    sections.push('# not a heading');
                    sections.push('```');
                    j += 2;
                } else {
                    sections.push(`body ${i}-${j} lorem ipsum dolor sit amet`);
                }
            }
        }
        const mdContent = sections.join('\n');
        const docPath = path.join(fixture, 'doc.md');
        await writeFile(docPath, mdContent);

        const result = await tool('peek_file', { path: docPath });
        assert.equal(result.kind, 'markdown');
        assert.equal(result.recommend, 'PARTIAL');
        assert.equal(result.entries.length, 30);
        assert.equal(result.entries[0].title, 'Section 1');
        assert.equal(result.entries[0].start, 1);
        assert.equal(result.entries[0].end, 11);
        assert.ok(!result.entries.some(e => e.title.includes('not a heading')));

        const ratio = JSON.stringify(result).length / mdContent.length;
        console.log(`PEEK_RATIO ${ratio.toFixed(3)}`);
        assert.ok(ratio < 0.35, `Expected ratio < 0.35, got ${ratio}`);
    });
    await check('read_section returns only the requested section', async () => {
        const docPath = path.join(fixture, 'doc.md');
        const sec12Start = 1 + (12 - 1) * 11;
        const sec12 = await tool('read_section', { path: docPath, query: 'section 12' });
        assert.equal(sec12.startLine, sec12Start);
        assert.ok(sec12.text.startsWith(`${sec12Start}: ## Section 12`));
        assert.ok(!sec12.text.includes('Section 13'));
        assert.equal(sec12.truncated, false);
        assert.equal(sec12.nextLine, null);

        const rangeShort = await tool('read_section', { path: docPath, startLine: 1, endLine: 3, maxChars: 1000 });
        assert.equal(rangeShort.text.split('\n').length, 3);

        const rangeLong = await tool('read_section', { path: docPath, startLine: 1, endLine: 330, maxChars: 1000 });
        assert.equal(rangeLong.truncated, true);
        assert.ok(rangeLong.nextLine > 1);
    });
    await check('peek_file outlines C# and falls back for text', async () => {
        const csPath = path.join(fixture, 'Sample.cs');
        const csCode = [
            'namespace MyNamespace;',
            '',
            'public class SampleClass',
            '{',
            '    public void MethodOne()',
            '    {',
            '        if (true) return;',
            '    }',
            '',
            '    private int MethodTwo(int value)',
            '    {',
            '        return value * 2;',
            '    }',
            '}'
        ].join('\n');
        await writeFile(csPath, csCode);

        const csResult = await tool('peek_file', { path: csPath });
        const titles = csResult.entries.map(e => e.title);
        assert.ok(titles.some(t => t.includes('SampleClass')));
        assert.ok(titles.some(t => t.includes('MethodOne')));
        assert.ok(titles.some(t => t.includes('MethodTwo')));
        assert.ok(!titles.some(t => t.startsWith('if')));

        const txtPath = path.join(fixture, 'plain.txt');
        const txtLines = Array.from({ length: 250 }, (_, i) => `line ${i + 1}`);
        await writeFile(txtPath, txtLines.join('\n'));

        const txtResult = await tool('peek_file', { path: txtPath });
        assert.equal(txtResult.kind, 'text');
        assert.equal(txtResult.entries.length, 3);
    });
    await check('read_section decodes Shift_JIS', async () => {
        const sjisPath = path.join(fixture, 'sjis.txt');
        await writeFile(sjisPath, Buffer.from('93fa967b8cea0d0a328d7396da', 'hex'));
        const sjisResult = await tool('read_section', { path: sjisPath, startLine: 1, endLine: 2 });
        assert.equal(sjisResult.encoding, 'shift_jis');
        assert.equal(sjisResult.text, '1: 日本語\n2: 2行目');
    });
    await check('summarize_log groups repeated errors', async () => {
        const logPath = path.join(fixture, 'build.log');
        const lines = [];
        let firstErrorLine = -1;
        let errorCount = 0;

        for (let i = 1; i <= 500; i++) {
            if (i === 500) {
                lines.push('done');
            } else if (i === 250) {
                lines.push('FATAL crash');
            } else if (errorCount < 40 && (i % 12 === 0 || (500 - i <= 40 - errorCount))) {
                if (firstErrorLine === -1) firstErrorLine = i;
                lines.push(`error CS1002: ; expected at line ${i}`);
                errorCount++;
            } else {
                lines.push(`info step ${i}`);
            }
        }
        assert.equal(errorCount, 40);
        assert.equal(lines.length, 500);

        const logContent = lines.join('\n');
        await writeFile(logPath, logContent);

        const logResult = await tool('summarize_log', { path: logPath });
        assert.equal(logResult.groups[0].count, 40);
        assert.equal(logResult.groups[0].firstLine, firstErrorLine);
        assert.ok(logResult.groups.some(g => g.text.includes('FATAL') && g.count === 1));
        assert.ok(logResult.tail[logResult.tail.length - 1].endsWith('done'));
        const jsonLength = JSON.stringify(logResult).length;
        assert.ok(jsonLength < 4000, `Expected JSON length < 4000, got ${jsonLength}`);

        const ratio = jsonLength / logContent.length;
        console.log(`LOG_RATIO ${ratio.toFixed(3)}`);
    });
    await check('file tools reject relative paths', async () => {
        const res = await tool('peek_file', { path: 'relative.md' });
        assert.equal(res.status, 'error');
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
    await check('complex objects return text and success omits empty fields', async () => {
        const started = performance.now();
        const files = await call('Get-Item .', { workingDirectory: fixture });
        assert.equal(typeof files.data, 'string', JSON.stringify(files).slice(0, 300));
        assert.ok(files.data.length < 3000, `data too large: ${files.data.length}`);
        assert.ok(performance.now() - started < 5000);
        const ok = await call('42');
        assert.deepEqual(Object.keys(ok).sort(), ['data', 'durationMs', 'status']);
    });
    await check('session limit evicts the least recently used idle session', async () => {
        for (let i = 0; i < 8; i++) assert.equal((await call('42', { sessionId: `lru-${i}` })).data, 42);
    });
    await check('persistent state and structured output', async () => {
        assert.equal((await call('$global:inazumaValue = 42; $inazumaValue')).data, 42);
        assert.equal((await call('$inazumaValue')).data, 42);
        assert.deepEqual((await call('[pscustomobject]@{ answer = 42; nested = @{ ok = $true } }')).data,
            { answer: 42, nested: { ok: true } });
    });
    await check('compact response preserves content and reduces duplication', async () => {
        const script = "'sample-' * 200";
        const regular = await call(script, { compactResponse: false });
        const compact = await call(script, { compactResponse: true });
        assert.equal(compact.data, regular.data);
        assert.equal(compact.output, undefined);
        assert.ok(JSON.stringify(compact).length < JSON.stringify(regular).length * 0.6);
        const structured = await call('[pscustomobject]@{ answer = 42 }', { compactResponse: true });
        assert.deepEqual(structured.data, { answer: 42 });
        assert.equal(structured.output, undefined);
        const truncated = await call("'x' * 3000", { compactResponse: true, maxOutputChars: 1000 });
        assert.equal(truncated.isTruncated, true);
        assert.equal(truncated.data, undefined);
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
        assert.equal(await realpath((await call('(Get-Location).Path', { workingDirectory: fixture })).data), await realpath(fixture));
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
        assert.notEqual((await call(process.platform === 'win32' ? "cmd /c 'exit 7'" : "sh -c 'exit 7'")).status, 'success');
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
        const failed = await tool('run_dotnet_build', { workingDirectory: project, contextLines: 1 });
        assert.equal(failed.status, 'error', JSON.stringify(failed));
        assert.notEqual(failed.exitCode, 0);
        assert.ok(failed.errors.some(error => error.ruleId === 'CS0103'), JSON.stringify(failed));
        assert.equal(failed.errors.find(error => error.ruleId === 'CS0103').codeContext, '>1| MissingThing();', JSON.stringify(failed));
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
    await check('contextLines adds clamped source context to failures', async () => {
        const project = path.join(fixture, 'context');
        const cliDirectory = path.join(project, 'node_modules', 'vitest');
        await mkdir(cliDirectory, { recursive: true });
        const specPath = path.join(project, 'ctx.spec.ts');
        const source = Array.from({ length: 30 }, (_, index) => index === 14 ? `expect(1).toBe(2); // ${'x'.repeat(300)}` : `line${index + 1}`);
        await writeFile(specPath, source.join('\n'));
        const spec = specPath.replaceAll('\\', '/');
        const failure = line => ({ fullName: `fails ${line}`, status: 'failed', failureMessages: [`Error: boom\n    at ${spec}:${line}:1`] });
        const payload = { success: false, testResults: [{ name: spec, status: 'failed', message: '', assertionResults: [failure(15), failure(1), failure(30)] }] };
        await writeFile(path.join(cliDirectory, 'vitest.mjs'), `import fs from 'node:fs'; const out = process.argv.find(a => a.startsWith('--outputFile=')).slice(13); fs.writeFileSync(out, JSON.stringify(${JSON.stringify(payload)})); process.exitCode = 1;`);
        const withContext = await tool('run_vitest', { workingDirectory: project, contextLines: 2 });
        const [middle, first, last] = withContext.failures;
        assert.match(middle.codeContext, /^ 13\| line13\n 14\| line14\n>15\| expect\(1\)\.toBe\(2\); \/\/ x+\.\.\.\n 16\| line16\n 17\| line17$/, middle.codeContext);
        assert.ok(middle.codeContext.split('\n')[2].length <= 170);
        assert.match(first.codeContext, /^>1\| line1\n 2\| line2\n 3\| line3$/, first.codeContext);
        assert.match(last.codeContext, /\n>30\| line30$/, last.codeContext);
        const huge = await tool('run_vitest', { workingDirectory: project, contextLines: 99 });
        assert.equal(huge.failures[0].codeContext.split('\n').length, 21);
        assert.ok(JSON.stringify(huge).length <= 8000);
        for (const lines of [0, -3]) assert.equal((await tool('run_vitest', { workingDirectory: project, contextLines: lines })).failures[0].codeContext, undefined);
        await rm(withContext.fullLogPath);

        const run = async (files, contextLines) => {
            const results = files.map(([file, line]) => ({ name: file, status: 'failed', message: '', assertionResults: [{ fullName: `fails ${path.basename(file)}`, status: 'failed', failureMessages: [`Error: boom\n    at ${file.replaceAll('\\', '/')}:${line}:1`] }] }));
            await writeFile(path.join(cliDirectory, 'vitest.mjs'), `import fs from 'node:fs'; const out = process.argv.find(a => a.startsWith('--outputFile=')).slice(13); fs.writeFileSync(out, JSON.stringify(${JSON.stringify({ success: false, testResults: results })})); process.exitCode = 1;`);
            return tool('run_vitest', { workingDirectory: project, contextLines });
        };
        const sjisPath = path.join(project, 'sjis.ts');
        await writeFile(sjisPath, Buffer.concat([Buffer.from('const a = 1;\n// '), Buffer.from([0x93, 0xfa, 0x96, 0x7b, 0x8c, 0xea]), Buffer.from('\nconst b = 2;\n')]));
        const utf16Path = path.join(project, 'utf16.ts');
        await writeFile(utf16Path, Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from('const a = 1;\r\n// 日本語\r\nconst b = 2;\r\n', 'utf16le')]));
        const dependencyPath = path.join(project, 'node_modules', 'pkg', 'index.ts');
        await mkdir(path.dirname(dependencyPath), { recursive: true });
        await writeFile(dependencyPath, 'line1\nline2\n');
        const outsidePath = path.join(fixture, 'outside.ts');
        await writeFile(outsidePath, 'line1\nline2\n');
        const encoded = await run([[sjisPath, 2], [utf16Path, 2], [dependencyPath, 1], [outsidePath, 1], [path.join(project, 'missing.ts'), 1]], 1);
        const contexts = encoded.failures.map(failure => failure.codeContext);
        assert.equal(contexts[0], ' 1| const a = 1;\n>2| // 日本語\n 3| const b = 2;', JSON.stringify(encoded));
        assert.equal(contexts[1], contexts[0]);
        assert.deepEqual(contexts.slice(2), [undefined, undefined, undefined]);

        const widePaths = [];
        for (let index = 0; index < 10; index++) {
            widePaths.push([path.join(project, `wide${index}.ts`), 11]);
            await writeFile(widePaths[index][0], Array.from({ length: 21 }, () => 'z'.repeat(200)).join('\n'));
        }
        const wide = await run(widePaths, 10);
        assert.ok(JSON.stringify(wide).length <= 8000, String(JSON.stringify(wide).length));
        assert.ok(wide.failures[0].codeContext.length <= 2400);
        assert.equal(wide.failures.at(-1).codeContext, undefined);
        assert.ok(wide.failures.every(failure => failure.file && failure.line === 11), JSON.stringify(wide));
    });
    await check('run_tsc parses diagnostics, counts and rejects managed arguments', async () => {
        const project = path.join(fixture, 'tsc');
        const libDirectory = path.join(project, 'node_modules', 'typescript', 'lib');
        await mkdir(libDirectory, { recursive: true });
        await writeFile(path.join(project, 'a.ts'), 'const a: number = "x";\nconst b = 1;');
        const mock = path.join(libDirectory, 'tsc.js');
        const script = body => `if (!process.argv.includes('--noEmit') || process.argv[process.argv.indexOf('--pretty') + 1] !== 'false') process.exit(9);\n${body}`;
        const errors = Array.from({ length: 20 }, (_, index) => `src/f${index}.ts(${index + 1},5): error TS${index % 2 ? 2322 : 2304}: Problem ${index}${'y'.repeat(400)}\n  Type 'string' is not assignable.`);
        errors.unshift('a.ts(1,7): error TS2322: Type \'string\' is not assignable to type \'number\'.');
        await writeFile(mock, script(`console.log(${JSON.stringify(errors.join('\n'))}); process.exitCode = 2;`));
        const failed = await tool('run_tsc', { workingDirectory: project, contextLines: 1 });
        assert.equal(failed.status, 'error', JSON.stringify(failed));
        assert.equal(failed.errorCount, 21);
        assert.deepEqual(failed.errorCodeCounts, { TS2322: 11, TS2304: 10 });
        assert.equal(failed.diagnostics.length > 0 && failed.diagnostics.length <= 15, true);
        assert.equal(failed.diagnostics[0].file, 'a.ts');
        assert.equal(failed.diagnostics[0].line, 1);
        assert.equal(failed.diagnostics[0].column, 7);
        assert.equal(failed.diagnostics[0].code, 'TS2322');
        assert.match(failed.diagnostics[0].codeContext, /^>1\| const a: number = "x";\n 2\| const b = 1;$/);
        assert.ok(failed.diagnostics.every(item => item.message.length <= 323));
        assert.ok(JSON.stringify(failed).length <= 8000);
        await rm(failed.fullLogPath);

        await writeFile(mock, script('process.exitCode = 0;'));
        const success = await tool('run_tsc', { workingDirectory: project });
        assert.equal(success.status, 'success', JSON.stringify(success));
        assert.equal(success.errorCount, 0);
        await rm(success.fullLogPath);

        await writeFile(mock, script("console.log('error TS5057: Cannot find a tsconfig.json file at the specified directory.'); process.exitCode = 1;"));
        const noConfig = await tool('run_tsc', { workingDirectory: project });
        assert.equal(noConfig.status, 'error');
        assert.equal(noConfig.diagnostics[0].code, 'TS5057');
        assert.equal(noConfig.diagnostics[0].file, undefined);
        await rm(noConfig.fullLogPath);

        await writeFile(mock, script("console.log('internal crash'); process.exitCode = 1;"));
        const crash = await tool('run_tsc', { workingDirectory: project });
        assert.equal(crash.status, 'error');
        assert.equal(crash.errorCount, 0);
        await rm(crash.fullLogPath);

        for (const argument of ['--pretty', '--watch', '-w', '--noEmit', '--preserveWatchOutput'])
            assert.match((await tool('run_tsc', { workingDirectory: project, tscArgs: [argument] })).message, /managed by this tool/);
        await writeFile(mock, "process.exitCode = process.argv.includes('--noEmit') || !process.argv.includes('--build') ? 9 : 0;");
        const built = await tool('run_tsc', { workingDirectory: project, tscArgs: ['--build'] });
        assert.equal(built.status, 'success', JSON.stringify(built));
        await rm(built.fullLogPath);
        assert.equal((await tool('run_tsc', { workingDirectory: fixture })).status, 'error');
        assert.match((await tool('run_tsc', { workingDirectory: project, tscPath: 'relative.js' })).message, /absolute/);
    });
    await check('run_dotnet_test parses TRX, groups same-cause failures and suggests a filter', async () => {
        const project = path.join(fixture, 'dotnet-test');
        await mkdir(project);
        const source = path.join(project, 'CalcTests.cs');
        await writeFile(source, 'line1\nAssert.Equal(1, 2);\nline3\n');
        // dotnet test runs the VSTest target, so this project stands in for a real test project.
        await writeFile(path.join(project, 'fake.proj'), `<Project><Target Name="Restore" /><Target Name="VSTest"><Copy SourceFiles="$(MSBuildProjectDirectory)/sample.trx" DestinationFiles="$(VSTestResultsDirectory)/out.trx" /><Error Text="Test Run Failed." Condition="Exists('$(MSBuildProjectDirectory)/fail')" /></Target></Project>`);
        const failedResult = (name, line) => `<UnitTestResult testName="${name}" outcome="Failed" duration="00:00:00.0100000"><Output><ErrorInfo><Message>Assert.Equal() Failure</Message><StackTrace>   at Xunit.Assert.Equal()\n   at Demo.CalcTests.Adds() in ${source}:line ${line}</StackTrace></ErrorInfo></Output></UnitTestResult>`;
        const trx = results => `<?xml version="1.0" encoding="utf-8"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>${results}</Results></TestRun>`;
        const passedResult = '<UnitTestResult testName="Demo.CalcTests.Ok" outcome="Passed" duration="00:00:00.0050000" />';
        await writeFile(path.join(project, 'sample.trx'), trx(failedResult('Demo.CalcTests.Adds(x: 1)', 2) + failedResult('Demo.CalcTests.Adds(x: 2)', 2) + failedResult('Demo.CalcTests.Other', 3) + passedResult + '<UnitTestResult testName="Demo.CalcTests.Skip" outcome="NotExecuted" />'));
        await writeFile(path.join(project, 'fail'), '');
        const failed = await tool('run_dotnet_test', { workingDirectory: project, testArgs: ['fake.proj'], contextLines: 1 });
        assert.equal(failed.status, 'error', JSON.stringify(failed));
        assert.deepEqual([failed.stats.passed, failed.stats.failed, failed.stats.skipped, failed.stats.total], [1, 3, 1, 5]);
        assert.equal(failed.failures.length, 2);
        assert.deepEqual([failed.failures[0].file, failed.failures[0].line, failed.failures[0].count], ['CalcTests.cs', 2, 2]);
        assert.equal(failed.failures[1].count, undefined);
        assert.equal(failed.failures[0].errors[0].message, 'Assert.Equal() Failure');
        assert.equal(failed.failures[0].codeContext, ' 1| line1\n>2| Assert.Equal(1, 2);\n 3| line3');
        assert.deepEqual(failed.rerunArgs, ['--filter', 'FullyQualifiedName~Demo.CalcTests.Adds|FullyQualifiedName~Demo.CalcTests.Other']);
        await rm(failed.fullLogPath);

        await rm(path.join(project, 'fail'));
        await writeFile(path.join(project, 'sample.trx'), trx(passedResult));
        const success = await tool('run_dotnet_test', { workingDirectory: project, testArgs: ['fake.proj'] });
        assert.equal(success.status, 'success', JSON.stringify(success));
        assert.equal(success.rerunArgs, undefined);
        await rm(success.fullLogPath);

        await writeFile(path.join(project, 'sample.trx'), '<NotTrx />');
        const malformed = await tool('run_dotnet_test', { workingDirectory: project, testArgs: ['fake.proj'] });
        assert.equal(malformed.status, 'report_unavailable', JSON.stringify(malformed));
        await rm(malformed.fullLogPath);
        assert.match((await tool('run_dotnet_test', { workingDirectory: project, testArgs: ['--logger', 'html'] })).message, /managed by this tool/);
    });
    await check('same-cause failures are grouped and rerunArgs lists the failing files', async () => {
        const project = path.join(fixture, 'group');
        const cliDirectory = path.join(project, 'node_modules', 'vitest');
        await mkdir(cliDirectory, { recursive: true });
        const [a, b] = ['a.spec.ts', 'b.spec.ts'].map(name => path.join(project, name).replaceAll('\\', '/'));
        const failure = (title, file, line, message) => ({ fullName: title, status: 'failed', failureMessages: [`Error: ${message}\n    at ${file}:${line}:1`] });
        const payload = { success: false, testResults: [
            { name: a, status: 'failed', message: '', assertionResults: [failure('one', a, 3, 'same'), failure('two', a, 3, 'same'), failure('three', a, 9, 'other'), { fullName: 'ok', status: 'passed', failureMessages: [] }] },
            { name: b, status: 'failed', message: '', assertionResults: [failure('four', b, 1, 'b')] }
        ] };
        await writeFile(path.join(cliDirectory, 'vitest.mjs'), `import fs from 'node:fs'; const out = process.argv.find(a => a.startsWith('--outputFile=')).slice(13); fs.writeFileSync(out, JSON.stringify(${JSON.stringify(payload)})); process.exitCode = 1;`);
        const grouped = await tool('run_vitest', { workingDirectory: project });
        assert.equal(grouped.stats.failed, 4, JSON.stringify(grouped));
        assert.deepEqual(grouped.failures.map(item => [item.title, item.count]), [['one', 2], ['three', undefined], ['four', undefined]]);
        assert.deepEqual(grouped.rerunArgs, ['a.spec.ts', 'b.spec.ts']);
        await rm(grouped.fullLogPath);
    });
    await check('run_tsc onlyNew lists diagnostics added since the previous run', async () => {
        const project = path.join(fixture, 'tsc-diff');
        const libDirectory = path.join(project, 'node_modules', 'typescript', 'lib');
        await mkdir(libDirectory, { recursive: true });
        const mock = path.join(libDirectory, 'tsc.js');
        const emit = lines => writeFile(mock, `console.log(${JSON.stringify(lines.join('\n'))}); process.exitCode = 2;`);
        await emit(['a.ts(1,1): error TS2304: Cannot find name A.', 'b.ts(5,1): error TS2322: Type B.']);
        const first = await tool('run_tsc', { workingDirectory: project, onlyNew: true });
        assert.deepEqual([first.errorCount, first.diagnostics.length, first.newCount, first.fixedCount], [2, 2, undefined, undefined], JSON.stringify(first));
        await emit(['b.ts(9,1): error TS2322: Type B.', 'c.ts(2,1): error TS7006: Implicit any C.']);
        const second = await tool('run_tsc', { workingDirectory: project, onlyNew: true });
        assert.deepEqual([second.errorCount, second.newCount, second.fixedCount], [2, 1, 1], JSON.stringify(second));
        assert.deepEqual(second.diagnostics.map(item => item.code), ['TS7006']);
        const all = await tool('run_tsc', { workingDirectory: project });
        assert.deepEqual([all.diagnostics.length, all.newCount], [2, undefined]);
        for (const item of [first, second, all]) await rm(item.fullLogPath);
    });
    await check('run_eslint parses the JSON report and rejects managed arguments', async () => {
        const project = path.join(fixture, 'eslint');
        const binDirectory = path.join(project, 'node_modules', 'eslint', 'bin');
        await mkdir(binDirectory, { recursive: true });
        await mkdir(path.join(project, 'src'));
        await writeFile(path.join(project, 'src', 'a.js'), 'const x = 1;\ny();\n???\n');
        const mock = path.join(binDirectory, 'eslint.js');
        const script = (payload, exitCode) => `const fs=require('node:fs'); const a=process.argv; if (a[2] !== '.' || a[a.indexOf('--format') + 1] !== 'json') process.exit(9); fs.writeFileSync(a[a.indexOf('--output-file') + 1], JSON.stringify(${JSON.stringify(payload)})); process.exitCode=${exitCode};`;
        await writeFile(mock, script([{ filePath: path.join(project, 'src', 'a.js'), messages: [
            { ruleId: 'no-unused-vars', severity: 1, message: 'x is unused', line: 1, column: 7 },
            { ruleId: 'no-undef', severity: 2, message: 'y is not defined', line: 2, column: 1 },
            { ruleId: null, fatal: true, severity: 2, message: 'Parsing error', line: 3, column: 1 }
        ] }], 1));
        const failed = await tool('run_eslint', { workingDirectory: project, contextLines: 1 });
        assert.equal(failed.status, 'error', JSON.stringify(failed));
        assert.deepEqual([failed.errorCount, failed.warningCount], [2, 1]);
        assert.deepEqual(failed.ruleCounts, { 'no-undef': 1, 'no-unused-vars': 1 });
        assert.deepEqual(failed.diagnostics.map(item => [item.rule, item.severity, item.line]), [['no-undef', 'error', 2], [undefined, 'error', 3], ['no-unused-vars', 'warning', 1]]);
        assert.match(failed.diagnostics[0].file, /^src[\\/]a\.js$/);
        assert.equal(failed.diagnostics[0].codeContext, ' 1| const x = 1;\n>2| y();\n 3| ???');
        await rm(failed.fullLogPath);

        await writeFile(mock, script([], 0));
        const success = await tool('run_eslint', { workingDirectory: project });
        assert.equal(success.status, 'success', JSON.stringify(success));
        await rm(success.fullLogPath);

        await writeFile(mock, "console.error('config crash'); process.exitCode = 2;");
        const crash = await tool('run_eslint', { workingDirectory: project });
        assert.equal(crash.status, 'error', JSON.stringify(crash));
        await rm(crash.fullLogPath);
        for (const argument of ['--format', '-f', '--output-file', '-o'])
            assert.match((await tool('run_eslint', { workingDirectory: project, eslintArgs: [argument] })).message, /managed by this tool/);
        assert.equal((await tool('run_eslint', { workingDirectory: fixture })).status, 'error');
    });
    await check('search_files returns bounded per-file matches', async () => {
        const project = path.join(fixture, 'search');
        await mkdir(path.join(project, 'sub'), { recursive: true });
        await mkdir(path.join(project, 'node_modules', 'x'), { recursive: true });
        await writeFile(path.join(project, 'a.cs'), 'class Foo {}\n// TODO one\n// todo two\n');
        await writeFile(path.join(project, 'sub', 'b.ts'), 'const todo = 1;\n');
        await writeFile(path.join(project, 'node_modules', 'x', 'c.cs'), '// TODO hidden\n');
        await writeFile(path.join(project, 'bin.dat'), Buffer.concat([Buffer.from([0, 1, 2, 0]), Buffer.from('TODO binary')]));
        await writeFile(path.join(project, 'sjis.txt'), Buffer.concat([Buffer.from('TODO '), Buffer.from([0x93, 0xfa, 0x96, 0x7b, 0x8c, 0xea]), Buffer.from('\n')]));
        const all = await tool('search_files', { path: project, pattern: 'todo' });
        assert.deepEqual([all.status, all.filesMatched, all.totalMatches, all.truncated], ['success', 3, 4, undefined], JSON.stringify(all));
        assert.deepEqual(all.files, [
            { file: 'a.cs', count: 2, matches: ['2| // TODO one', '3| // todo two'] },
            { file: 'sjis.txt', count: 1, matches: ['1| TODO 日本語'] },
            { file: 'sub/b.ts', count: 1, matches: ['1| const todo = 1;'] }
        ]);
        assert.deepEqual((await tool('search_files', { path: project, pattern: 'todo', fileName: '*.cs' })).files.map(item => item.file), ['a.cs']);
        assert.equal((await tool('search_files', { path: project, pattern: 'TODO', caseSensitive: true })).totalMatches, 2);
        const limited = await tool('search_files', { path: project, pattern: 'todo', maxResults: 1 });
        assert.deepEqual([limited.truncated, limited.files[0].matches.length, limited.files[1].matches], [true, 1, undefined]);
        assert.equal((await tool('search_files', { path: path.join(project, 'a.cs'), pattern: 'Foo' })).files[0].file, 'a.cs');
        assert.equal((await tool('search_files', { path: project, pattern: 'nothing-here' })).totalMatches, 0);
        assert.match((await tool('search_files', { path: project, pattern: '(' })).message, /Invalid regex/);
        assert.equal((await tool('search_files', { path: 'relative', pattern: 'x' })).status, 'error');

        const wide = path.join(project, 'wide');
        await mkdir(wide);
        for (let index = 0; index < 60; index++)
            await writeFile(path.join(wide, `file${String(index).padStart(2, '0')}.txt`), Array.from({ length: 10 }, () => `needle ${'w'.repeat(300)}`).join('\n'));
        const bounded = await tool('search_files', { path: wide, pattern: 'needle', maxResults: 100 });
        assert.deepEqual([bounded.filesMatched, bounded.totalMatches, bounded.files.length, bounded.truncated], [60, 600, 40, true]);
        assert.ok(JSON.stringify(bounded).length <= 8000, String(JSON.stringify(bounded).length));
    });
    await check('tool calls are recorded in the usage log', async () => {
        const entries = (await readFile(path.join(tmpdir(), 'inazuma-pwsh-logs', 'usage.jsonl'), 'utf8')).trim().split('\n').map(line => JSON.parse(line));
        for (const name of ['run_eslint', 'run_dotnet_test', 'peek_file', 'summarize_log'])
            assert.ok(entries.some(entry => entry.tool === name && entry.sourceBytes > 0 && entry.responseBytes > 0 && entry.time), `${name} is not recorded`);
        assert.ok(!entries.some(entry => entry.tool === 'search_files' && entry.responseBytes));
    });
    await check('pytest JUnit XML parsing with a fake interpreter', async () => {
        const project = path.join(fixture, 'pytest');
        await mkdir(project);
        const fakePython = path.join(project, process.platform === 'win32' ? 'fake-python.cmd' : 'fake-python');
        await writeFile(path.join(project, 'fake-pytest.mjs'), `import fs from 'node:fs'; const out = process.argv.find(a => a.startsWith('--junitxml=')).slice(11); if (process.argv[2] !== '-m' || process.argv[3] !== 'pytest') process.exit(9); const mode = fs.readFileSync('mode.txt', 'utf8').trim(); const payloads = JSON.parse(fs.readFileSync('payloads.json', 'utf8')); fs.writeFileSync(out, payloads[mode].xml); process.exitCode = payloads[mode].exitCode;`);
        await writeFile(fakePython, process.platform === 'win32' ? '@node "%~dp0fake-pytest.mjs" %*\r\n' : '#!/bin/sh\nexec node "$(dirname "$0")/fake-pytest.mjs" "$@"\n', { mode: 0o755 });
        await writeFile(path.join(project, 'payloads.json'), JSON.stringify({
            failed: { exitCode: 1, xml: `<?xml version="1.0" encoding="utf-8"?><testsuites name="pytest tests"><testsuite name="pytest" errors="1" failures="1" skipped="1" tests="4" time="0.5"><testcase classname="test_sample" name="test_passes" time="0.001" /><testcase classname="test_sample" name="test_fails" time="0.001"><failure message="AssertionError: assert 1 == 2">def test_fails():\n&gt;       assert 1 == 2\nE       AssertionError: assert 1 == 2\n\ntest_sample.py:9: AssertionError</failure></testcase><testcase classname="test_sample" name="test_skipped" time="0"><skipped type="pytest.skip" message="demo">test_sample.py:12: demo</skipped></testcase><testcase classname="test_sample" name="test_setup" time="0"><error message="failed on setup">file ${path.join(project, 'test_sample.py')}, line 17\n  def test_setup(missing_fixture):\nE       fixture 'missing_fixture' not found</error></testcase></testsuite></testsuites>` },
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
        const relative = await tool('run_pytest', { workingDirectory: project, pythonPath: process.platform === 'win32' ? 'python.exe' : 'python3' });
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
    const startWith = env => new Promise((resolve, reject) => {
        const child = spawn(executable, [], { stdio: ['pipe', 'pipe', 'pipe'], env });
        let output = '';
        const timer = setTimeout(() => { child.kill(); reject(new Error(`server did not respond: ${output}`)); }, 60000);
        child.stderr.on('data', chunk => { output += chunk; });
        createInterface({ input: child.stdout }).once('line', () => { clearTimeout(timer); child.kill(); resolve({ started: true, output }); });
        child.once('exit', code => { clearTimeout(timer); resolve({ started: false, code, output }); });
        child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: {
            protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'pwsh-path-test', version: '1' }
        } }) + '\n');
    });
    await check('invalid explicit pwsh path explains the fix', async () => {
        const started = await startWith({ ...process.env, INAZUMA_PWSH_PATH: path.join(fixture, 'missing', 'pwsh.exe') });
        assert.equal(started.started, false, started.output);
        assert.equal(started.code, 1, started.output);
        assert.match(started.output, /InazumaPwsh cannot start: .*inazumaPwsh\.pwshPath/);
        assert.doesNotMatch(started.output, /Unhandled exception/);
    });
    if (process.platform === 'win32') {
        await check('Windows finds pwsh outside PATH', async () => {
            const candidates = [
                path.join(process.env.ProgramFiles ?? '', 'PowerShell', '7', 'pwsh.exe'),
                path.join(process.env.LOCALAPPDATA ?? '', 'Microsoft', 'WindowsApps', 'pwsh.exe'),
                path.join(process.env.USERPROFILE ?? '', '.dotnet', 'tools', 'pwsh.exe')
            ];
            const installed = (await Promise.all(candidates.map(file => lstat(file).then(() => true, () => false)))).some(Boolean);
            const env = { ...process.env, PATH: path.join(process.env.SystemRoot ?? 'C:\\Windows', 'System32') };
            delete env.INAZUMA_PWSH_PATH;
            delete env.Path;
            const started = await startWith(env);
            assert.equal(started.started, installed, started.output);
            if (!installed) assert.match(started.output, /standard install locations/);
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

