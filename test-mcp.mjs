import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { lstat, mkdir, mkdtemp, readFile, realpath, rm, stat, writeFile } from 'node:fs/promises';
import { homedir, tmpdir } from 'node:os';
import path from 'node:path';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { runInNewContext } from 'node:vm';

const root = path.dirname(fileURLToPath(import.meta.url));
const hostRid = process.platform === 'win32' ? 'win-x64'
  : process.platform === 'darwin' ? (process.arch === 'arm64' ? 'osx-arm64' : 'osx-x64')
  : (process.arch === 'arm64' ? 'linux-arm64' : 'linux-x64');
const executable = process.argv[2] ?? path.join(root, 'bin/Debug/net10.0', hostRid, process.platform === 'win32' ? 'InazumaPwsh.exe' : 'InazumaPwsh');
const spawnStartedAt = performance.now();
const server = spawn(executable, process.argv.slice(3), {
    stdio: ['pipe', 'pipe', 'pipe'], env: { ...process.env, PSModulePath: '', ZUMA_WORKSPACE_ROOT: root }
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
// peek_file returns each entry as "start-end level title".
function outline(peeked) {
    return peeked.entries.map(entry => {
        const match = /^(\d+)-(\d+) (\d+) (.*)$/s.exec(entry);
        assert.ok(match, `unexpected entry: ${entry}`);
        return { start: Number(match[1]), end: Number(match[2]), level: Number(match[3]), title: match[4] };
    });
}
const startedAt = performance.now();
let passed = 0;
async function check(name, run) {
    if (process.env.INAZUMA_TEST_FILTER && !name.includes(process.env.INAZUMA_TEST_FILTER)) return;
    if (process.env.INAZUMA_OUTLINE_ONLY && !name.startsWith('outline regression')) return;
    if (process.env.INAZUMA_USAGE_ONLY && name !== 'usage accounting for unmeasured calls') return;
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
    await check('file reading regression outlines expression members and decorators', async () => {
        const cs = path.join(fixture, 'expression.cs');
        await writeFile(cs, ['public class Sample', '{', '    public int Add(int a, int b) => a + b;', '    public int X => 1;', '    public void Declared();', '}'].join('\n'));
        const entries = outline(await tool('peek_file', { path: cs }));
        assert.ok(entries.some(entry => entry.title.includes('Add(')));
        assert.ok(entries.some(entry => entry.title.includes('X =>')));
        assert.ok(!entries.some(entry => entry.title.includes('Declared(')));
        assert.deepEqual(outline(await tool('peek_file', { path: cs, maxLevel: 1 })).map(entry => entry.title), ['public class Sample']);
        const py = path.join(fixture, 'decorated.py');
        await writeFile(py, ['def previous():', '    pass', '@pytest.mark.skip', '@other(', '    reason=")", # closing parenthesis in a string', ')', '@staticmethod', 'def decorated():', '    pass', '@klass', 'class Next:', '    pass'].join('\n'));
        const python = outline(await tool('peek_file', { path: py }));
        assert.deepEqual(python.map(entry => [entry.start, entry.end]), [[1, 2], [3, 9], [10, 12]]);
    });
    await check('file reading regression supports ranges and useful missing section suggestions', async () => {
        const doc = path.join(fixture, 'range-fixture.md');
        await writeFile(doc, '# Alpha section\nfirst body\n## Beta heading\nunique-body-token\n# Gamma\nlast');
        assert.match((await tool('read_section', { path: doc, startLine: 4, endLine: 2 })).message, /startLine.*endLine/);
        const fuzzy = await tool('read_section', { path: doc, query: 'Alphx' });
        assert.equal(fuzzy.status, 'error');
        assert.equal(fuzzy.candidates[0], 'Alpha section');
        assert.ok(fuzzy.candidates.length <= 10);
        const body = await tool('read_section', { path: doc, query: 'unique-body-token' });
        assert.equal(body.bodyMatches[0].line, 4);
        const multi = await tool('read_section', { path: doc, ranges: [{ startLine: 2, endLine: 2 }, 'Gamma', { query: 'Beta' }] });
        assert.equal(multi.status, 'success');
        assert.deepEqual(multi.ranges.map(item => item.result.startLine), [2, 5, 3]);
        assert.deepEqual(multi.ranges.map(item => item.label), ['2-2', 'Gamma', 'Beta']);
    });
    await check('file reading regression distributes literal search and respects ignores', async () => {
        const project = path.join(fixture, 'search-regression');
        await mkdir(path.join(project, 'node_modules'), { recursive: true });
        assert.equal(spawnSync('git', ['init', '-q', project]).status, 0);
        await writeFile(path.join(project, '.gitignore'), 'ignored.txt\n');
        await writeFile(path.join(project, 'a.txt'), 'before\n    a.b\n    a.b\n    a.b\nafter');
        await writeFile(path.join(project, 'b.txt'), 'before\n  a.b\nafter');
        await writeFile(path.join(project, 'ignored.txt'), 'a.b');
        await writeFile(path.join(project, 'excluded.txt'), 'a.b');
        await writeFile(path.join(project, 'node_modules', 'skip.txt'), 'a.b');
        await writeFile(path.join(project, 'regex.txt'), 'axb');
        const result = await tool('search_files', { path: project, pattern: 'a.b', literal: true, contextLines: 1, maxPerFile: 2, maxResults: 2, exclude: ['excluded.txt'] });
        assert.equal(result.status, 'success', JSON.stringify(result));
        assert.equal(result.totalMatches, 4);
        assert.deepEqual(result.files.map(file => file.file), ['a.txt', 'b.txt']);
        assert.deepEqual(result.files.map(file => file.matches.length), [1, 1]);
        assert.equal(result.files[0].omitted, 2);
        assert.equal(result.files[0].matches[0], '2|     a.b');
        assert.deepEqual(result.files[0].context[0].before, ['1| before']);
        assert.deepEqual(result.files[0].context[0].after, ['3|     a.b']);
        assert.ok((await tool('search_files', { path: project, pattern: 'a.b' })).totalMatches > result.totalMatches);
    });
    await check('file reading regression detects Japanese log issues without zero summaries', async () => {
        const log = path.join(fixture, 'japanese-build.log');
        await writeFile(log, ['0 エラー', '0 個の警告', '0 件のエラー', '0 Error(s)', '0 Warning(s)', 'エラー 発生', '警告 発生', '処理失敗', '例外 発生', '0 エラー 1 警告'].join('\n'));
        const result = await tool('summarize_log', { path: log, contextLines: 0 });
        assert.equal(result.matchedLines, 5, JSON.stringify(result));
        assert.deepEqual(result.groups.map(group => group.firstLine), [6, 7, 8, 9, 10]);
    });
    await check('terminating errors retain preceding output', async () => {
        for (const script of ["'before'; throw 'boom'", "'before'; $ErrorActionPreference='Stop'; Get-Item -LiteralPath 'missing-zuma-test-file'"]) {
            const response = await call(script, { sessionId: 'terminating', workingDirectory: fixture });
            assert.equal(response.status, 'error', JSON.stringify(response));
            assert.match(response.errors.join('\n'), /boom|missing-zuma-test-file/i);
            assert.match(JSON.stringify(response.data), /before/);
        }
    });
    await check('working location persists and reset restores workspace', async () => {
        const sessionId = 'location-test';
        const coldStarted = performance.now();
        assert.equal(await realpath((await call('(Get-Location).Path', { sessionId })).data), await realpath(root));
        console.log(`MCP_COLD_SESSION_MS ${(performance.now() - coldStarted).toFixed(1)}`);
        await call('Set-Location -LiteralPath ' + literal(fixture), { sessionId });
        assert.equal(await realpath((await call('(Get-Location).Path', { sessionId })).data), await realpath(fixture));
        await call('42', { sessionId, workingDirectory: root });
        assert.equal(await realpath((await call('(Get-Location).Path', { sessionId })).data), await realpath(root));
        await call('42', { sessionId, workingDirectory: fixture });
        assert.equal((await tool('reset_powershell_session', { sessionId })).status, 'success');
        assert.equal(await realpath((await call('(Get-Location).Path', { sessionId })).data), await realpath(root));
    });
    await check('information streams preserve output order', async () => {
        const response = await call("'one'; Write-Host 'two'; 'three'; Write-Information 'four' -InformationAction Continue; 'five'; Write-Verbose 'details' -Verbose; Write-Debug 'debugging' -Debug; Write-Warning 'warning'", { sessionId: 'streams-test' });
        assert.equal(response.status, 'success', JSON.stringify(response));
        assert.deepEqual(response.data, ['one', 'two', 'three', 'four', 'five']);
        assert.ok(response.warnings.some(value => value.includes('VERBOSE: details')));
        assert.ok(response.warnings.some(value => value.includes('DEBUG: debugging')));
        assert.ok(response.warnings.some(value => value.includes('warning')));
    });
    await check('truncated multi-line output has a plain text log', async () => {
        const response = await call('1..300 | ForEach-Object { "line-$_-" + ("x" * 10) }', { sessionId: 'log-test', maxOutputChars: 1000 });
        assert.equal(response.isTruncated, true, JSON.stringify(response));
        const lines = (await readFile(response.fullLogPath, 'utf8')).split(/\r?\n/);
        assert.equal(lines.length, 300);
        assert.equal(lines[0], 'line-1-xxxxxxxxxx');
        assert.equal(lines[299], 'line-300-xxxxxxxxxx');
        const found = await tool('search_files', { path: response.fullLogPath, pattern: '^line-300-' });
        assert.equal(found.totalMatches, 1);
        await rm(response.fullLogPath);
        const objects = await call('1..300 | ForEach-Object { [pscustomobject]@{ name = "object-$_"; value = "xxxxxxxxxx" } }', { sessionId: 'log-test', maxOutputChars: 1000 });
        assert.equal(objects.isTruncated, true, JSON.stringify(objects));
        const objectLines = (await readFile(objects.fullLogPath, 'utf8')).split(/\r?\n/);
        assert.equal(objectLines.length, 300);
        assert.match(objectLines[299], /object-300/);
        await rm(objects.fullLogPath);
    });
    await check('session management lists and enforces the limit', async () => {
        const absent = await tool('reset_powershell_session', { sessionId: 'absent-session' });
        assert.deepEqual([absent.status, absent.existed], ['success', false]);
        for (let index = 0; index < 6; index++) {
            const response = await call(`$global:marker = ${index}`, { sessionId: `limit-${index}`, workingDirectory: fixture });
            assert.equal(response.status, 'success', JSON.stringify(response));
        }
        let listing = await tool('list_powershell_sessions', {});
        assert.equal(listing.sessions.length, 6, JSON.stringify(listing));
        const first = listing.sessions.find(item => item.sessionId === 'limit-0');
        assert.equal(await realpath(first.currentLocation), await realpath(fixture));
        assert.ok(!Number.isNaN(Date.parse(first.createdAt)) && !Number.isNaN(Date.parse(first.lastUsedAt)));
        assert.equal(first.busy, false);
        assert.equal((await call('42', { sessionId: 'limit-6' })).data, 42);
        listing = await tool('list_powershell_sessions', {});
        assert.equal(listing.sessions.length, 6);
        assert.ok(!listing.sessions.some(item => item.sessionId === 'limit-0'));
        assert.equal((await tool('reset_powershell_session', { sessionId: 'limit-6' })).existed, true);
        const running = Array.from({ length: 6 }, (_, index) => call('Start-Sleep -Seconds 4', { sessionId: `limit-${index + 1}` }));
        const deadline = Date.now() + 3000;
        do {
            listing = await tool('list_powershell_sessions', {});
            if (listing.sessions.every(item => item.busy)) break;
            assert.ok(Date.now() < deadline, 'sessions did not become busy');
            await new Promise(resolve => setTimeout(resolve, 20));
        } while (true);
        const rejected = await call('42', { sessionId: 'limit-extra' });
        assert.equal(rejected.status, 'error');
        assert.match(rejected.errors.join(' '), /limit reached/i);
        await Promise.all(running);
    });
    await check('extension passes first local workspace and updates changes', async () => {
        const require = createRequire(path.join(root, 'vscode-extension', 'extension.js'));
        const disposable = { dispose() {} };
        let provider, changed, workspaceChanged;
        let changes = 0;
        const vscode = {
            EventEmitter: class { constructor() { this.event = () => disposable; } fire() { changes++; } dispose() {} },
            workspace: {
                workspaceFolders: [{ uri: { scheme: 'file', fsPath: fixture } }, { uri: { scheme: 'file', fsPath: root } }],
                getConfiguration: () => ({ get: () => '' }),
                onDidChangeConfiguration: callback => { changed = callback; return disposable; },
                onDidChangeWorkspaceFolders: callback => { workspaceChanged = callback; return disposable; }
            },
            lm: { registerMcpServerDefinitionProvider: (_, value) => { provider = value; return disposable; } },
            McpStdioServerDefinition: class { constructor(label, command, args, env) { Object.assign(this, { label, command, args, env }); } },
            Uri: { file: value => value }, StatusBarAlignment: { Right: 0 },
            window: { createStatusBarItem: () => ({ hide() {}, show() {}, dispose() {} }) },
            commands: { registerCommand: () => disposable }
        };
        const sandbox = { module: { exports: {} }, process, require: name => {
            if (name === 'vscode') return vscode;
            if (name === 'node:fs') return { watchFile() {}, unwatchFile() {} };
            return require(name);
        } };
        runInNewContext(await readFile(path.join(root, 'vscode-extension', 'extension.js'), 'utf8'), sandbox);
        const context = { extensionPath: root, subscriptions: [] };
        sandbox.module.exports.activate(context);
        assert.equal(provider.provideMcpServerDefinitions()[0].env.ZUMA_WORKSPACE_ROOT, fixture);
        vscode.workspace.workspaceFolders = [];
        workspaceChanged();
        assert.equal(changes, 1);
        assert.equal(provider.provideMcpServerDefinitions()[0].env, undefined);
        changed({ affectsConfiguration: () => true });
        assert.equal(changes, 2);
        for (const subscription of context.subscriptions) subscription.dispose();
    });
    await check('missing workspace uses the user home', async () => {
        const env = { ...process.env, ZUMA_WORKSPACE_ROOT: path.join(fixture, 'missing') };
        const child = spawn(executable, [], { stdio: ['pipe', 'pipe', 'pipe'], env });
        const lines = createInterface({ input: child.stdout })[Symbol.asyncIterator]();
        const sendChild = async (id, method, params) => {
            child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
            return JSON.parse((await lines.next()).value);
        };
        try {
            const init = await sendChild(1, 'initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'home-test', version: '1' } });
            assert.ok(init.result?.serverInfo);
            child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} }) + '\n');
            const response = result(await sendChild(2, 'tools/call', { name: 'execute_powershell', arguments: { script: '(Get-Location).Path', sessionId: 'home-test' } }));
            assert.equal(await realpath(response.data), await realpath(homedir()));
        } finally { child.stdin.end(); child.kill(); }
    });
    await check('public tool descriptions are in English', async () => {
        for (const name of ['execute_powershell', 'reset_powershell_session', 'run_dotnet_build', 'run_dotnet_test', 'run_playwright', 'run_affected_tests', 'inspect_playwright_trace', 'run_bun_test', 'run_pytest', 'run_vitest', 'run_tsc', 'run_eslint', 'run_cargo_check', 'peek_file', 'read_section', 'summarize_log', 'search_files']) {
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
        assert.deepEqual(outline(result).map(entry => entry.title), ['First', 'Second', 'Third']);
        assert.equal(outline(result)[1].end, 10);
    });
    await check('outline regression parses PowerShell declarations and ranges', async () => {
        const scriptPath = path.join(fixture, 'script.ps1');
        await writeFile(scriptPath, [
            'function Outer {', "@'", 'function Ghost {}', "'@", '<#', 'function Commented {}', '#>',
            'function Inner {', "  '}'", '}', '}', "function After { 'ok' }"
        ].join('\n'));
        const result = await tool('peek_file', { path: scriptPath });
        assert.deepEqual(outline(result).map(entry => [entry.title, entry.start, entry.end, entry.level]), [
            ['function Outer {', 1, 11, 1], ['function Inner {', 8, 10, 2], ["function After { 'ok' }", 12, 12, 1]
        ]);
        assert.deepEqual(result.entries, ['1-11 1 function Outer {', '8-10 2 function Inner {', "12-12 1 function After { 'ok' }"]);
        const section = await tool('read_section', { path: scriptPath, query: 'Outer' });
        assert.equal(section.endLine, 11);
    });
    await check('outline regression does not execute PowerShell source', async () => {
        const scriptPath = path.join(fixture, 'module.psm1');
        const markerPath = path.join(fixture, 'must-not-exist');
        await writeFile(scriptPath, [`Set-Content -Path ${literal(markerPath)} -Value 'executed'`,
            'class Widget {', '    [void] Run() {', '    }', '}'].join('\n'));
        const result = await tool('peek_file', { path: scriptPath });
        assert.deepEqual(outline(result).map(entry => [entry.start, entry.end, entry.level]), [[2, 5, 1], [3, 4, 2]]);
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
        assert.equal(outline(result)[0].title, 'Section 1');
        assert.equal(outline(result)[0].start, 1);
        assert.equal(outline(result)[0].end, 11);
        assert.ok(!outline(result).some(e => e.title.includes('not a heading')));
        // The string form is shorter than the former object form even for a single entry.
        const single = path.join(fixture, 'single.md');
        await writeFile(single, '# Only\nbody\n');
        const one = await tool('peek_file', { path: single });
        assert.deepEqual(one.entries, ['1-2 1 Only']);
        assert.ok(JSON.stringify(one.entries).length <= JSON.stringify(outline(one)).length);

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
        const titles = outline(csResult).map(e => e.title);
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
        // The matched line is returned once (text); context holds only the lines around it.
        for (const group of logResult.groups) {
            const matched = `${group.firstLine}: ${group.text}`;
            assert.ok(!group.context.includes(matched), JSON.stringify(group));
            const restored = [...group.context, matched].sort((left, right) => parseInt(left) - parseInt(right));
            const first = Math.max(1, group.firstLine - 1);
            assert.deepEqual(restored, lines.slice(first - 1, Math.min(500, group.firstLine + 1)).map((line, offset) => `${first + offset}: ${line}`));
        }
        const noContext = await tool('summarize_log', { path: logPath, contextLines: 0 });
        assert.deepEqual(noContext.groups.map(group => group.context), logResult.groups.map(() => []));
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
        assert.equal(await realpath((await call('(Get-Location).Path', { workingDirectory: fixture })).data), await realpath(fixture));
        assert.equal(await realpath((await call('(Get-Location).Path')).data), await realpath(fixture));
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
    await check('run_dotnet_build onlyNew lists newly introduced diagnostics', async () => {
        const project = path.join(fixture, 'build-diff');
        await mkdir(project);
        await writeFile(path.join(project, 'Probe.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup></Project>');
        const source = path.join(project, 'Program.cs');
        await writeFile(source, 'MissingThing();');
        const first = await tool('run_dotnet_build', { workingDirectory: project, onlyNew: true });
        assert.deepEqual([first.summary.totalErrors, first.errors.length, first.newCount, first.fixedCount], [1, 1, undefined, undefined], JSON.stringify(first));

        await writeFile(source, '\n\nMissingThing();');
        await writeFile(path.join(project, 'Other.cs'), 'class Other { void Run() { MissingOther(); } }');
        const second = await tool('run_dotnet_build', { workingDirectory: project, onlyNew: true });
        assert.equal(second.summary.totalErrors, 2, JSON.stringify(second));
        assert.equal(second.newCount, 1, JSON.stringify(second));
        assert.equal(second.fixedCount, 0, JSON.stringify(second));
        assert.ok(second.errors.some(item => item.ruleId === 'CS0103' && item.path.endsWith('Other.cs')), JSON.stringify(second));
        assert.ok(second.errors.every(item => !item.path.endsWith('Program.cs')), JSON.stringify(second));
        assert.equal(first.summary.totalErrors, 1);
        await rm(first.fullLogPath);
        await rm(second.fullLogPath);
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
        assert.match(failure.output, /fixture failure/);
        await rm(failure.fullLogPath);
    });
    await check('Playwright attachments resolve to absolute paths', async () => {
        const project = path.join(fixture, 'playwright-attachments');
        const cliDirectory = path.join(project, 'node_modules', 'playwright');
        await mkdir(cliDirectory, { recursive: true });
        const payload = { suites: [{ file: 'sample.spec.ts', specs: [{ title: 'fails', tests: [{ status: 'unexpected', results: [{ status: 'failed', attachments: [{ name: 'trace', path: 'test-results/trace.zip', contentType: 'application/zip' }] }] }] }] }] };
        await writeFile(path.join(cliDirectory, 'cli.js'), `const fs=require('node:fs'); fs.writeFileSync(process.env.PLAYWRIGHT_JSON_OUTPUT_NAME, ${JSON.stringify(JSON.stringify(payload))}); process.exitCode=1;`);
        const response = await tool('run_playwright', { workingDirectory: project });
        const attachmentPath = response.failures[0].attachments[0].path;
        const expectedPaths = [project, await realpath(project)].map(directory => path.join(directory, 'test-results', 'trace.zip'));
        assert.ok(expectedPaths.includes(attachmentPath), attachmentPath);
        await rm(response.fullLogPath);
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
        assert.equal(giant.failures[0].omittedAttachments, 2);
        assert.ok(giant.fullLogPath, 'fullLogPath must exist');
        if (giant.fullReportPath) {
            const fullReport = JSON.parse(await readFile(giant.fullReportPath, 'utf8'));
            assert.equal(fullReport.failures[0].attachments.length, 2);
            await rm(giant.fullReportPath);
        }
        await rm(giant.fullLogPath);
    });
    await check('diagnostic budget keeps the first failure detailed and summarizes later failures', async () => {
        const project = path.join(fixture, 'vitest-budget');
        const cliDirectory = path.join(project, 'node_modules', 'vitest');
        await mkdir(cliDirectory, { recursive: true });
        const spec = path.join(project, 'sample.spec.ts').replaceAll('\\', '/');
        const stackFrames = index => Array.from({ length: 4 }, (_, frame) => `    at frame${index}-${frame} (${spec}:${index + 1}:${frame + 1}) ${'S'.repeat(240)}`).join('\n');
        const failures = Array.from({ length: 10 }, (_, index) => ({
            fullName: `primary failure ${index}`,
            status: 'failed',
            failureMessages: [`AssertionError: ${index === 0 ? 'D'.repeat(500) : 'later failure ' + 'E'.repeat(400)}\n${stackFrames(index)}`]
        }));
        const payload = { success: false, startTime: 1000, testResults: [{
            name: spec, status: 'failed', message: '', endTime: 1250,
            assertionResults: failures
        }] };
        await writeFile(path.join(cliDirectory, 'vitest.mjs'), `import fs from 'node:fs'; const out = process.argv.find(a => a.startsWith('--outputFile.json=')).slice(18); fs.writeFileSync(out, JSON.stringify(${JSON.stringify(payload)})); process.exitCode = 1;`);
        const result = await tool('run_vitest', { workingDirectory: project });
        assert.ok(JSON.stringify(result).length <= 8000, `response exceeded 8000 chars: ${JSON.stringify(result).length}`);
        assert.equal(result.failures.length, 10, JSON.stringify(result));
        assert.ok(result.failures[0].errors[0].stack.length > 800, 'first stack must remain detailed');
        assert.equal(result.failures[0].summary, undefined);
        assert.ok(result.failures.slice(1).every(failure => failure.summary && failure.errors === undefined && failure.stack === undefined), JSON.stringify(result.failures.slice(1)));
        assert.match(result.failures[1].summary, /sample\.spec\.ts:2/);
        await rm(result.fullLogPath);
    });
    await check('Vitest JSON failures, file errors and managed reporter', async () => {
        const project = path.join(fixture, 'vitest');
        const cliDirectory = path.join(project, 'node_modules', 'vitest');
        await mkdir(cliDirectory, { recursive: true });
        const cliPath = path.join(cliDirectory, 'vitest.mjs');
        const spec = path.join(project, 'math.spec.ts').replaceAll('\\', '/');
        const writer = payload => `import fs from 'node:fs'; const out = process.argv.find(a => a.startsWith('--outputFile.json=')).slice(18); if (process.argv[2] !== 'run' || !process.argv.includes('--reporter=default')) process.exit(9); fs.writeFileSync(out, JSON.stringify(${JSON.stringify(payload)})); process.exitCode = ${payload.success ? 0 : 1};`;
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
    await check('diagnostic compactResponse preserves unexplained Vitest output', async () => {
        const project = path.join(fixture, 'vitest-compact');
        const cliDirectory = path.join(project, 'node_modules', 'vitest');
        await mkdir(cliDirectory, { recursive: true });
        const payload = { testResults: [{ name: path.join(project, 'sample.ts'), status: 'failed', assertionResults: [{ fullName: 'fails', status: 'failed', failureMessages: ['AssertionError: expected 1 to be 2'] }] }] };
        await writeFile(path.join(cliDirectory, 'vitest.mjs'), `import fs from 'node:fs'; const out=process.argv.find(a=>a.startsWith('--outputFile.json=')).slice(18); fs.writeFileSync(out, JSON.stringify(${JSON.stringify(payload)})); console.log('AssertionError: expected 1 to be 2\\nTest Files  1 failed\\nunknown warning'); process.exitCode=1;`);
        const compact = await tool('run_vitest', { workingDirectory: project });
        const raw = await tool('run_vitest', { workingDirectory: project, compactResponse: false });
        assert.doesNotMatch(compact.output, /AssertionError/);
        assert.match(compact.output, /Test Files  1 failed/);
        assert.match(compact.output, /unknown warning/);
        assert.match(raw.output, /AssertionError/);
        await rm(compact.fullLogPath);
        await rm(raw.fullLogPath);
        await writeFile(path.join(cliDirectory, 'vitest.mjs'), `import fs from 'node:fs'; const out=process.argv.find(a=>a.startsWith('--outputFile.json=')).slice(18); fs.writeFileSync(out, JSON.stringify(${JSON.stringify(payload)})); console.log(${JSON.stringify('⎯⎯ Failed Tests 1 ⎯⎯\nFAIL sample.ts\n- Expected\n+ Received\n⎯⎯ Unhandled Errors ⎯⎯\nUnhandled error: external service failed\nTest Files  1 failed\nJSON report written to temporary.json')}); process.exitCode=1;`);
        const external = await tool('run_vitest', { workingDirectory: project });
        assert.match(external.output, /Unhandled error: external service failed/);
        assert.match(external.output, /Test Files  1 failed/);
        assert.doesNotMatch(external.output, /Failed Tests|Expected|Received|JSON report written/);
        await rm(external.fullLogPath);
        await writeFile(path.join(cliDirectory, 'vitest.mjs'), `import fs from 'node:fs'; const out=process.argv.find(a=>a.startsWith('--outputFile.json=')).slice(18); fs.writeFileSync(out, JSON.stringify(${JSON.stringify(payload)})); for (let index=0; index<80; index++) console.log('error line ' + index + ' ${'x'.repeat(50)}'); process.exitCode=1;`);
        const long = await tool('run_vitest', { workingDirectory: project, compactResponse: false });
        assert.equal(long.output, Array.from({ length: 80 }, (_, index) => `error line ${index} ${'x'.repeat(50)}\n`).join(''));
        await rm(long.fullLogPath);
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
        await writeFile(path.join(cliDirectory, 'vitest.mjs'), `import fs from 'node:fs'; const out = process.argv.find(a => a.startsWith('--outputFile.json=')).slice(18); fs.writeFileSync(out, JSON.stringify(${JSON.stringify(payload)})); process.exitCode = 1;`);
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
            await writeFile(path.join(cliDirectory, 'vitest.mjs'), `import fs from 'node:fs'; const out = process.argv.find(a => a.startsWith('--outputFile.json=')).slice(18); fs.writeFileSync(out, JSON.stringify(${JSON.stringify({ success: false, testResults: results })})); process.exitCode = 1;`);
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

        // Lines already returned for an earlier failure of the same file are not repeated.
        const nearPath = path.join(project, 'near.ts');
        const otherPath = path.join(project, 'other.ts');
        const nearSource = Array.from({ length: 20 }, (_, index) => `near${index + 1}`);
        await writeFile(nearPath, nearSource.join('\n'));
        await writeFile(otherPath, nearSource.join('\n'));
        const near = await run([[nearPath, 10], [nearPath, 11], [otherPath, 11], [nearPath, 16]], 2);
        assert.deepEqual(near.failures.map(failure => failure.codeContext), [
            '  8| near8\n  9| near9\n>10| near10\n 11| near11\n 12| near12',
            '>11| near11\n 13| near13',
            '  9| near9\n 10| near10\n>11| near11\n 12| near12\n 13| near13',
            ' 14| near14\n 15| near15\n>16| near16\n 17| near17\n 18| near18'
        ]);
        // Every former window can be rebuilt from the lines returned so far for the same file.
        const returned = new Map();
        for (const failure of near.failures) {
            const known = returned.get(failure.file) ?? new Map();
            returned.set(failure.file, known);
            for (const entry of failure.codeContext.split('\n')) known.set(Number(entry.slice(1).split('|')[0]), entry.slice(entry.indexOf('| ') + 2));
            assert.ok(failure.codeContext.split('\n').some(entry => entry.startsWith(`>${failure.line}| `)));
            for (let number = failure.line - 2; number <= failure.line + 2; number++) assert.equal(known.get(number), nearSource[number - 1], `${failure.file}:${number}`);
        }
        const alone = await run([[nearPath, 10]], 2);
        assert.equal(alone.failures[0].codeContext, near.failures[0].codeContext);
    });
    await check('run_vitest regressionsOnly lists tests that changed from passing to failing', async () => {
        const project = path.join(fixture, 'vitest-regressions');
        const cliDirectory = path.join(project, 'node_modules', 'vitest');
        await mkdir(cliDirectory, { recursive: true });
        const spec = path.join(project, 'sample.spec.ts').replaceAll('\\', '/');
        const cli = path.join(cliDirectory, 'vitest.mjs');
        const emit = results => writeFile(cli, `import fs from 'node:fs'; const out=process.argv.find(a=>a.startsWith('--outputFile.json=')).slice(18); fs.writeFileSync(out, JSON.stringify({success:false,startTime:1,endTime:2,testResults:[{name:${JSON.stringify(spec)},status:'failed',endTime:2,assertionResults:${JSON.stringify(results)}}]})); process.exitCode=1;`);
        const assertion = (name, status) => status === 'passed'
            ? { fullName: name, title: name, status: 'passed', failureMessages: [] }
            : { fullName: name, title: name, status: 'failed', failureMessages: [`Error: ${name} failed\n    at ${spec}:1:1`] };
        await emit([assertion('regression', 'passed'), assertion('existing failure', 'failed')]);
        const first = await tool('run_vitest', { workingDirectory: project, regressionsOnly: true });
        assert.equal(first.stats.failed, 1, JSON.stringify(first));
        assert.equal(first.regressionCount, undefined);
        await emit([assertion('regression', 'failed'), assertion('existing failure', 'failed')]);
        const second = await tool('run_vitest', { workingDirectory: project, regressionsOnly: true });
        assert.equal(second.stats.failed, 2, JSON.stringify(second));
        assert.equal(second.regressionCount, 1, JSON.stringify(second));
        assert.deepEqual(second.failures.map(item => item.title), ['regression']);
        assert.deepEqual(second.rerunArgs, ['sample.spec.ts']);
        assert.equal(second.passedTests, undefined);
        await rm(first.fullLogPath);
        await rm(second.fullLogPath);
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
        assert.ok(failed.diagnostics[0].message.length <= 323);
        assert.equal(failed.diagnostics[0].summary, undefined);
        assert.ok(failed.diagnostics.slice(1).every(item => item.summary && item.message === undefined), JSON.stringify(failed.diagnostics.slice(1)));
        assert.match(failed.diagnostics[1].summary, /src\/f0\.ts:1/);
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
        assert.equal(noConfig.output, '');
        await rm(noConfig.fullLogPath);

        // output keeps only what diagnostics does not already list in full.
        const raw = [
            'src/a.ts(3,5): error TS2322: Type A.',
            "  Type 'string' is not assignable.",
            `src/b.ts(7,1): error TS2304: Long ${'z'.repeat(400)}`,
            'error TS6053: File not found.',
            'src/a.ts(9,2): error TS2345: Argument B.',
            'Some trailing note'
        ];
        await writeFile(mock, script(`console.log(${JSON.stringify(raw.join('\n'))}); process.exitCode = 2;`));
        const trimmed = await tool('run_tsc', { workingDirectory: project });
        assert.equal(trimmed.errorCount, 4);
        assert.deepEqual(trimmed.output.trim().split(/\r?\n/), [raw[2], 'Some trailing note']);
        const rebuilt = trimmed.diagnostics.map(item => `${item.file ? `${item.file}(${item.line},${item.column}): ` : ''}error ${item.code}: ${item.message}`);
        assert.deepEqual(rebuilt, [`${raw[0]} ${raw[1].trim()}`, `${raw[2].slice(0, raw[2].indexOf('Long') + 320)}...`, raw[3], raw[4]]);
        await rm(trimmed.fullLogPath);

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
        const failedResult = (name, line) => `<UnitTestResult testName="${name}" outcome="Failed" duration="00:00:00.0100000"><Output><ErrorInfo><Message>Assert.Equal() Failure&#13;&#10;Expected 1</Message><StackTrace>   at Xunit.Assert.Equal()\n   at System.Reflection.MethodBaseInvoker.Invoke()\n   at System.RuntimeMethodHandle.InvokeMethod()\n   at Demo.CalcTests.Adds() in ${source}:line ${line}\n--- End of stack trace from previous location ---</StackTrace></ErrorInfo></Output></UnitTestResult>`;
        const trx = results => `<?xml version="1.0" encoding="utf-8"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>${results}</Results><ResultSummary><RunInfos><RunInfo outcome="Error"><Text>[FAIL] Demo.CalcTests.Adds</Text></RunInfo></RunInfos></ResultSummary></TestRun>`;
        const passedResult = '<UnitTestResult testName="Demo.CalcTests.Ok" outcome="Passed" duration="00:00:00.0050000" />';
        await writeFile(path.join(project, 'sample.trx'), trx(failedResult('Demo.CalcTests.Adds(x: 1)', 2) + failedResult('Demo.CalcTests.SameCause(x: 2)', 2) + failedResult('Demo.CalcTests.Other', 3) + passedResult + '<UnitTestResult testName="Demo.CalcTests.Skip" outcome="NotExecuted" />'));
        await writeFile(path.join(project, 'fail'), '');
        const failed = await tool('run_dotnet_test', { workingDirectory: project, testArgs: ['fake.proj'], contextLines: 1 });
        assert.equal(failed.status, 'error', JSON.stringify(failed));
        assert.deepEqual([failed.stats.passed, failed.stats.failed, failed.stats.skipped, failed.stats.total], [1, 3, 1, 5]);
        assert.equal(failed.failures.length, 2);
        assert.deepEqual([failed.failures[0].file, failed.failures[0].line, failed.failures[0].count], ['CalcTests.cs', 2, 2]);
        assert.equal(failed.failures[1].count, undefined);
        assert.equal(failed.failures[0].errors[0].message, 'Assert.Equal() Failure\nExpected 1');
        assert.match(failed.failures[0].errors[0].stack, /Demo\.CalcTests\.Adds/);
        assert.doesNotMatch(failed.failures[0].errors[0].stack, /Xunit|System\.Reflection|RuntimeMethodHandle|End of stack trace/);
        assert.equal(failed.failures[0].codeContext, ' 1| line1\n>2| Assert.Equal(1, 2);\n 3| line3');
        assert.deepEqual(failed.globalErrors, []);
        assert.deepEqual(failed.rerunArgs, ['--filter', 'FullyQualifiedName~Demo.CalcTests.Adds|FullyQualifiedName~Demo.CalcTests.SameCause|FullyQualifiedName~Demo.CalcTests.Other']);
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
    await check('run_affected_tests selects imported Vitest tests from git changes', async () => {
        const project = path.join(fixture, 'affected-tests');
        const cliDirectory = path.join(project, 'node_modules', 'vitest');
        const marker = path.join(fixture, 'affected-selected-tests.txt');
        await mkdir(path.join(project, 'src'), { recursive: true });
        await mkdir(path.join(project, 'tests'), { recursive: true });
        await mkdir(cliDirectory, { recursive: true });
        assert.equal(spawnSync('git', ['init', '-q', project]).status, 0);
        await writeFile(path.join(project, 'package.json'), JSON.stringify({ devDependencies: { vitest: '1.0.0' } }));
        await writeFile(path.join(project, 'src', 'math.ts'), 'export const add = (a: number, b: number) => a + b;\n');
        await writeFile(path.join(project, 'tests', 'math.spec.ts'), "import { add } from '../src/math';\nvoid add;\n");
        await writeFile(path.join(project, 'tests', 'other.spec.ts'), "import { value } from '../src/other';\nvoid value;\n");
        await writeFile(path.join(project, 'src', 'other.ts'), 'export const value = 1;\n');
        await writeFile(path.join(cliDirectory, 'vitest.mjs'), `import fs from 'node:fs'; const args=process.argv.slice(3); fs.writeFileSync(${JSON.stringify(marker)}, args.join('\\n')); const out=process.argv.find(a=>a.startsWith('--outputFile.json=')).slice(18); fs.writeFileSync(out, JSON.stringify({success:true,testResults:[{name:args[0],status:'passed',assertionResults:[{fullName:'affected',status:'passed',failureMessages:[]}]}]}));`);
        assert.equal(spawnSync('git', ['-C', project, 'add', '.']).status, 0);
        assert.equal(spawnSync('git', ['-C', project, '-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'initial']).status, 0);
        await writeFile(path.join(project, 'src', 'math.ts'), 'export const add = (a: number, b: number) => a + b + 0;\n');

        const response = await tool('run_affected_tests', { workingDirectory: project });
        assert.equal(response.status, 'success', JSON.stringify(response));
        assert.deepEqual(response.changedFiles, ['src/math.ts']);
        assert.deepEqual(response.selectedTests.map(item => item.file), ['tests/math.spec.ts']);
        assert.equal((await readFile(marker, 'utf8')).split(/\r?\n/)[0], 'tests/math.spec.ts');
        assert.doesNotMatch(await readFile(marker, 'utf8'), /other\.spec\.ts/);

        await writeFile(path.join(project, 'src', 'math.ts'), 'export const add = (a: number, b: number) => a + b;\n');
        const untrackedTest = path.join(project, 'tests', 'new.spec.ts');
        await writeFile(untrackedTest, 'export {};\n');
        const untracked = await tool('run_affected_tests', { workingDirectory: project });
        assert.deepEqual(untracked.changedFiles, ['tests/new.spec.ts']);
        assert.deepEqual(untracked.selectedTests.map(item => item.file), ['tests/new.spec.ts']);
        assert.equal((await readFile(marker, 'utf8')).split(/\r?\n/)[0], 'tests/new.spec.ts');

        await rm(untrackedTest);
        const unchanged = await tool('run_affected_tests', { workingDirectory: project });
        assert.equal(unchanged.status, 'no_tests', JSON.stringify(unchanged));
        assert.deepEqual(unchanged.selectedTests, []);

        await writeFile(path.join(project, 'A.csproj'), '<Project />');
        await writeFile(path.join(project, 'B.csproj'), '<Project />');
        await writeFile(path.join(project, 'tests', 'AmbiguousTests.cs'), 'public class AmbiguousTests {}\n');
        const ambiguous = await tool('run_affected_tests', { workingDirectory: project });
        assert.equal(ambiguous.status, 'error', JSON.stringify(ambiguous));
        assert.match(ambiguous.message, /Multiple \.csproj files/);
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
        await writeFile(path.join(cliDirectory, 'vitest.mjs'), `import fs from 'node:fs'; const out = process.argv.find(a => a.startsWith('--outputFile.json=')).slice(18); fs.writeFileSync(out, JSON.stringify(${JSON.stringify(payload)})); process.exitCode = 1;`);
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
        assert.doesNotMatch(second.output ?? '', /TS2322|TS7006/);
        const unchanged = await tool('run_tsc', { workingDirectory: project, onlyNew: true, compactResponse: false });
        assert.equal(unchanged.newCount, 0);
        assert.doesNotMatch(unchanged.output ?? '', /TS2322|TS7006/);
        const all = await tool('run_tsc', { workingDirectory: project });
        assert.deepEqual([all.diagnostics.length, all.newCount], [2, undefined]);
        for (const item of [first, second, unchanged, all]) await rm(item.fullLogPath);
    });
    await check('run_eslint parses the JSON report and rejects managed arguments', async () => {
        const project = path.join(fixture, 'eslint');
        const binDirectory = path.join(project, 'node_modules', 'eslint', 'bin');
        await mkdir(binDirectory, { recursive: true });
        await mkdir(path.join(project, 'src'));
        await writeFile(path.join(project, 'src', 'a.js'), 'const x = 1;\ny();\n???\n');
        const mock = path.join(binDirectory, 'eslint.js');
        const script = (payload, exitCode) => `const fs=require('node:fs'); const a=process.argv; if (!['.','src'].includes(a[2]) || a[a.indexOf('--format') + 1] !== 'json') process.exit(9); fs.writeFileSync(a[a.indexOf('--output-file') + 1], JSON.stringify(${JSON.stringify(payload)})); process.exitCode=${exitCode};`;
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

        await writeFile(mock, script([{ filePath: path.join(project, 'src', 'a.js'), messages: [
            { ruleId: 'no-undef', severity: 2, message: 'y is not defined', line: 7, column: 1 },
            { ruleId: 'no-unused-vars', severity: 1, message: 'z is unused', line: 8, column: 7 }
        ] }], 1));
        const first = await tool('run_eslint', { workingDirectory: project, eslintArgs: ['src'], onlyNew: true });
        assert.deepEqual([first.errorCount, first.warningCount, first.newCount, first.fixedCount], [1, 1, undefined, undefined]);
        await writeFile(mock, script([{ filePath: path.join(project, 'src', 'a.js'), messages: [
            { ruleId: 'no-undef', severity: 2, message: 'y is not defined', line: 12, column: 1 },
            { ruleId: 'semi', severity: 2, message: 'Missing semicolon', line: 13, column: 20 }
        ] }], 1));
        const second = await tool('run_eslint', { workingDirectory: project, eslintArgs: ['src'], onlyNew: true });
        assert.deepEqual([second.errorCount, second.newCount, second.fixedCount], [2, 1, 1], JSON.stringify(second));
        assert.deepEqual(second.diagnostics.map(item => item.rule), ['semi']);
        await rm(first.fullLogPath);
        await rm(second.fullLogPath);

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
    await check('run_cargo_check parses diagnostics and onlyNew', async () => {
        if (spawnSync('cargo', ['--version'], { stdio: 'ignore' }).status !== 0) return;
        const project = path.join(fixture, 'cargo-check');
        await mkdir(path.join(project, 'src'), { recursive: true });
        await writeFile(path.join(project, 'Cargo.toml'), '[package]\nname = "zuma-cargo-check-fixture"\nversion = "0.1.0"\nedition = "2024"\n');
        const source = path.join(project, 'src', 'main.rs');
        await writeFile(source, 'fn main() { missing(); }\n');

        const first = await tool('run_cargo_check', { workingDirectory: project, onlyNew: true });
        assert.equal(first.status, 'error', JSON.stringify(first));
        assert.equal(first.errorCount, 1);
        assert.equal(first.diagnostics[0].code, 'E0425');
        assert.match(first.diagnostics[0].file, /src[\\/]main\.rs$/);
        assert.equal(first.diagnostics[0].line, 1);
        assert.equal(first.newCount, undefined);
        assert.equal(first.fixedCount, undefined);
        const raw = await tool('run_cargo_check', { workingDirectory: project, compactResponse: false });
        assert.match(raw.output, /"reason":"compiler-message"/);
        assert.match(raw.output, /E0425/);
        await rm(raw.fullLogPath);

        await writeFile(source, '\n\nfn main() {\n    missing();\n    let value: i32 = "wrong";\n    let _ = value;\n}\n');
        const second = await tool('run_cargo_check', { workingDirectory: project, onlyNew: true, contextLines: 1 });
        assert.equal(second.errorCount, 2, JSON.stringify(second));
        assert.equal(second.newCount, 1, JSON.stringify(second));
        assert.equal(second.fixedCount, 0, JSON.stringify(second));
        assert.deepEqual(second.diagnostics.map(item => item.code), ['E0308']);
        assert.match(second.diagnostics[0].codeContext, /let value: i32 = "wrong";/);
        assert.doesNotMatch(second.output ?? '', /E0425|E0308/);
        await rm(first.fullLogPath);
        await rm(second.fullLogPath);

        await writeFile(source, 'fn main() { let unused = 1; }\n');
        const warning = await tool('run_cargo_check', { workingDirectory: project });
        assert.equal(warning.status, 'success', JSON.stringify(warning));
        assert.equal(warning.errorCount, 0);
        assert.equal(warning.warningCount, 1);
        assert.equal(warning.diagnostics[0].code, 'unused_variables');
        assert.equal(warning.diagnostics[0].severity, 'warning');
        await rm(warning.fullLogPath);

        for (const argument of ['--message-format=json', '--color=never'])
            assert.match((await tool('run_cargo_check', { workingDirectory: project, cargoArgs: [argument] })).message, /managed by this tool/);
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
    await check('usage accounting for unmeasured calls', async () => {
        const usagePath = path.join(tmpdir(), 'inazuma-pwsh-logs', 'usage.jsonl');
        const before = await readFile(usagePath, 'utf8').catch(error => {
            if (error.code === 'ENOENT') return '';
            throw error;
        });
        const docPath = path.join(fixture, 'usage.txt');
        await writeFile(docPath, 'searchable content\n');
        assert.equal((await call('42')).status, 'success');
        assert.equal((await tool('search_files', { path: docPath, pattern: 'content' })).status, 'success');
        assert.equal((await tool('search_files', { path: docPath, pattern: '(' })).status, 'error');
        assert.equal((await tool('reset_powershell_session', {})).status, 'success');
        assert.equal((await tool('run_eslint', { workingDirectory: 'relative' })).status, 'error');
        assert.equal((await tool('peek_file', { path: docPath })).status, 'success');
        const entries = (await readFile(usagePath, 'utf8')).slice(before.length).trim().split('\n').map(line => JSON.parse(line));
        for (const [name, count] of [['execute_powershell', 1], ['search_files', 2], ['reset_powershell_session', 1], ['run_eslint', 1]]) {
            const matching = entries.filter(entry => entry.tool === name && entry.countOnly === true);
            assert.equal(matching.length, count, `${name} count`);
            assert.ok(matching.every(entry => entry.sourceBytes === undefined && entry.responseBytes === undefined));
        }
        assert.ok(entries.some(entry => entry.tool === 'peek_file' && entry.sourceBytes > 0 && entry.responseBytes > 0));
    });
    await check('tool calls are recorded in the usage log', async () => {
        const entries = (await readFile(path.join(tmpdir(), 'inazuma-pwsh-logs', 'usage.jsonl'), 'utf8')).trim().split('\n').map(line => JSON.parse(line));
        for (const name of ['run_eslint', 'run_dotnet_test', 'peek_file', 'summarize_log'])
            assert.ok(entries.some(entry => entry.tool === name && entry.sourceBytes > 0 && entry.responseBytes > 0 && entry.time), `${name} is not recorded`);
        assert.ok(entries.some(entry => entry.tool === 'search_files' && entry.countOnly === true));
    });
    await check('pytest JUnit XML parsing with a fake interpreter', async () => {
        const project = path.join(fixture, 'pytest');
        await mkdir(project);
        const fakePython = path.join(project, process.platform === 'win32' ? 'fake-python.cmd' : 'fake-python');
        await writeFile(path.join(project, 'fake-pytest.mjs'), `import fs from 'node:fs'; const out = process.argv.find(a => a.startsWith('--junitxml=')).slice(11); if (process.argv[2] !== '-m' || process.argv[3] !== 'pytest') process.exit(9); const mode = fs.readFileSync('mode.txt', 'utf8').trim(); const payloads = JSON.parse(fs.readFileSync('payloads.json', 'utf8')); fs.writeFileSync(out, payloads[mode === 'collection' ? 'failed' : mode].xml); if (mode === 'failed') console.log(${JSON.stringify('external warning\ntest_sample.py .F [100%]\n======== FAILURES ========\n________ test_fails ________\n> assert 1 == 2\nE AssertionError: assert 1 == 2\n----- generated xml file: report.xml -----\n===== short test summary info =====\nFAILED test_sample.py::test_fails\n===== 1 failed, 1 passed in 0.5s =====')}); if (mode === 'collection') console.log('===== ERRORS =====\\n_____ ERROR collecting test_broken.py _____\\nImportError: missing dependency\\n===== 1 error in 0.2s ====='); if (mode === 'dtd') console.log('raw crash detail\\nFAILURES'); process.exitCode = payloads[mode === 'collection' ? 'failed' : mode].exitCode;`);
        await writeFile(fakePython, process.platform === 'win32' ? '@node "%~dp0fake-pytest.mjs" %*\r\n' : '#!/bin/sh\nexec node "$(dirname "$0")/fake-pytest.mjs" "$@"\n', { mode: 0o755 });
        await writeFile(path.join(project, 'payloads.json'), JSON.stringify({
            failed: { exitCode: 1, xml: `<?xml version="1.0" encoding="utf-8"?><testsuites name="pytest tests"><testsuite name="pytest" errors="1" failures="1" skipped="1" tests="4" time="0.5"><testcase classname="test_sample" name="test_passes" time="0.001" /><testcase classname="test_sample" name="test_fails" time="0.001"><failure message="AssertionError: assert 1 == 2">def test_fails():\n&gt;       assert 1 == 2\nE       AssertionError: assert 1 == 2\n\ntest_sample.py:9: AssertionError</failure></testcase><testcase classname="test_sample" name="test_skipped" time="0"><skipped type="pytest.skip" message="demo">test_sample.py:12: demo</skipped></testcase><testcase classname="test_sample" name="test_setup" time="0"><error message="failed on setup">file ${path.join(project, 'test_sample.py')}, line 17\n  def test_setup(missing_fixture):\nE       fixture 'missing_fixture' not found</error></testcase></testsuite></testsuites>` },
            empty: { exitCode: 5, xml: '<?xml version="1.0" encoding="utf-8"?><testsuites name="pytest tests"><testsuite name="pytest" errors="0" failures="0" skipped="0" tests="0" time="0.01" /></testsuites>' },
            dtd: { exitCode: 0, xml: '<?xml version="1.0"?><!DOCTYPE x [<!ENTITY a "aaaaaaaaaa">]><testsuites><testsuite><testcase name="&a;" /></testsuite></testsuites>' }
        }));
        await writeFile(path.join(project, 'mode.txt'), 'failed');
        const failed = await tool('run_pytest', { workingDirectory: project, pythonPath: fakePython });
        assert.equal(failed.status, 'error', JSON.stringify(failed));
        assert.deepEqual([failed.stats.passed, failed.stats.failed, failed.stats.errors, failed.stats.skipped, failed.stats.total], [1, 1, 1, 1, 4]);
        assert.equal(failed.stats.duration, 500);
        assert.deepEqual(failed.failures.map(failure => [failure.title, failure.file, failure.line, failure.status]),
            [['test_sample > test_fails', 'test_sample.py', 9, 'failed'], ['test_sample > test_setup', 'test_sample.py', 17, 'error']]);
        assert.equal(failed.failures[0].errors[0].message, 'AssertionError: assert 1 == 2');
        assert.match(failed.output, /external warning/);
        assert.match(failed.output, /1 failed, 1 passed/);
        assert.doesNotMatch(failed.output, /FAILURES|test_fails|assert 1 == 2|generated xml|short test summary/);
        const raw = await tool('run_pytest', { workingDirectory: project, pythonPath: fakePython, compactResponse: false });
        assert.match(raw.output, /FAILURES/);
        assert.match(raw.output, /generated xml/);
        await rm(raw.fullLogPath);
        await rm(failed.fullLogPath);

        await writeFile(path.join(project, 'mode.txt'), 'collection');
        const collection = await tool('run_pytest', { workingDirectory: project, pythonPath: fakePython });
        assert.match(collection.output, /ERROR collecting test_broken.py/);
        assert.match(collection.output, /ImportError: missing dependency/);
        await rm(collection.fullLogPath);

        await writeFile(path.join(project, 'mode.txt'), 'empty');
        const empty = await tool('run_pytest', { workingDirectory: project, pythonPath: fakePython });
        assert.equal(empty.status, 'no_tests', JSON.stringify(empty));
        await rm(empty.fullLogPath);

        await writeFile(path.join(project, 'mode.txt'), 'dtd');
        const dtd = await tool('run_pytest', { workingDirectory: project, pythonPath: fakePython });
        assert.equal(dtd.status, 'report_unavailable', JSON.stringify(dtd));
        assert.equal(dtd.output, 'raw crash detail\nFAILURES\n');
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
            await writeFile(path.join(project, 'test_real.py'), 'def test_ok():\n    assert True\n\n\ndef test_bad():\n    assert 1 == 2\n\n\ndef test_error():\n    raise RuntimeError("boom")\n');
            const real = await tool('run_pytest', { workingDirectory: project, pythonPath: process.env.INAZUMA_TEST_PYTHON, testArgs: ['-p', 'no:cacheprovider'] });
            assert.equal(real.status, 'error', JSON.stringify(real));
            assert.deepEqual([real.stats.passed, real.stats.failed, real.stats.errors], [1, 1, 1], JSON.stringify(real));
            assert.equal(real.failures[0].file, 'test_real.py');
            assert.equal(real.failures[0].line, 6);
            await rm(real.fullLogPath);
        });
    }
    if (spawnSync('bun', ['--version'], { stdio: 'ignore' }).status === 0) {
        await check('real bun test JUnit report', async () => {
            const project = path.join(fixture, 'bun');
            await mkdir(project);
            await writeFile(path.join(project, 'sample.test.ts'), "import { describe, expect, test } from 'bun:test';\ndescribe('group', () => {\n    test('passes', () => { expect(1).toBe(1); });\n    test('fails', () => { console.log('warning: outside test frame\\n44 | synthetic frame\\n  at synthetic-stack'); expect(1).toBe(2); });\n    test.skip('skipped', () => {});\n});\n");
            const failed = await tool('run_bun_test', { workingDirectory: project });
            assert.equal(failed.status, 'error', JSON.stringify(failed));
            assert.deepEqual([failed.stats.passed, failed.stats.failed, failed.stats.skipped], [1, 1, 1]);
            assert.deepEqual([failed.failures[0].title, failed.failures[0].file, failed.failures[0].line], ['group > fails', 'sample.test.ts', 4]);
            assert.match(failed.output, /Ran 3 tests/);
            assert.match(failed.output, /warning: outside test frame/);
            assert.doesNotMatch(failed.output, /\(pass\)|\(fail\)|\(skip\)|expect\(1\)|at sample|synthetic frame|synthetic-stack/);
            const raw = await tool('run_bun_test', { workingDirectory: project, compactResponse: false });
            assert.match(raw.output, /\(fail\)/);
            assert.match(raw.output, /expect\(1\)/);
            await rm(raw.fullLogPath);
            await rm(failed.fullLogPath);
            await writeFile(path.join(project, 'sample.test.ts'), "import { expect, test } from 'bun:test';\ntest('passes', () => { expect(1).toBe(1); });\n");
            const success = await tool('run_bun_test', { workingDirectory: project });
            assert.equal(success.status, 'success', JSON.stringify(success));
            await rm(success.fullLogPath);
            const noMatches = await tool('run_bun_test', { workingDirectory: project, testArgs: ['-t', 'no-such-test-12345'] });
            assert.notEqual(noMatches.status, 'success', JSON.stringify(noMatches));
            assert.ok(noMatches.globalErrors.length > 0, JSON.stringify(noMatches));
            await rm(noMatches.fullLogPath);
            assert.match((await tool('run_bun_test', { workingDirectory: project, testArgs: ['--reporter=dots'] })).message, /managed by this tool/);
        });
    }
    if (process.env.ZUMA_TRIAL_ROOT) {
        await check('real trial diagnostic tools', async () => {
            const project = process.env.ZUMA_TRIAL_ROOT;
            const dotnet = path.join(project, 'dotnet');
            const cases = [
                ['bun', 'run_bun_test', { workingDirectory: project, testArgs: ['tests/calc.test.ts'] }],
                ['vitest', 'run_vitest', { workingDirectory: project, testArgs: ['vt/calc.spec.ts'] }],
                ['pytest', 'run_pytest', { workingDirectory: path.join(project, 'py') }],
                ['playwright', 'run_playwright', { workingDirectory: project, testArgs: ['pw/demo.spec.ts'] }],
                ['dotnet', 'run_dotnet_test', { workingDirectory: dotnet, testArgs: ['ZumaTests.csproj', '--no-restore'] }],
                ['build', 'run_dotnet_build', { workingDirectory: dotnet, target: 'ZumaTests.csproj', includeWarnings: true, contextLines: 1 }],
                ['tsc', 'run_tsc', { workingDirectory: project, onlyNew: true }],
                ['eslint', 'run_eslint', { workingDirectory: project, eslintArgs: ['lint'] }]
            ];
            for (const [label, name, args] of cases) {
                const response = await tool(name, args);
                console.log(`TRIAL ${label} ${JSON.stringify(response)}`);
                assert.notEqual(response.message, 'workingDirectory must be an existing absolute directory.', label);
                if (['bun', 'vitest', 'pytest', 'playwright', 'dotnet'].includes(label)) {
                    const raw = await tool(name, { ...args, compactResponse: false });
                    console.log(`TRIAL_SIZE ${label} raw=${Buffer.byteLength(raw.output ?? '')} compact=${Buffer.byteLength(response.output ?? '')}`);
                    assert.ok(response.failures?.length, JSON.stringify(response));
                    assert.ok(Buffer.byteLength(response.output ?? '') < Buffer.byteLength(raw.output ?? ''), label);
                    const unwanted = {
                        bun: /\(fail\)|\(skip\)|\d+\s*\|/,
                        vitest: /Failed Tests|JSON report written|Expected|Received/,
                        pytest: /FAILURES|short test summary info|generated xml file|test_fail/,
                        playwright: /Error Context:|attachment #|Call log:|Usage:/,
                        dotnet: /\[SKIP\]|Error Message:|Stack Trace:|Results File:/
                    };
                    assert.doesNotMatch(response.output ?? '', unwanted[label]);
                    assert.match(response.output ?? '', /failed|fail/i);
                    if (raw.fullLogPath) await rm(raw.fullLogPath);
                }
                if (response.fullLogPath) await rm(response.fullLogPath);
            }
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
    await check('run_dotnet_build captures missing project MSB1009', async () => {
        const response = await tool('run_dotnet_build', { workingDirectory: fixture, target: 'does-not-exist.csproj' });
        assert.equal(response.status, 'error');
        assert.equal(response.summary.totalErrors, 1, JSON.stringify(response));
        assert.equal(response.errors[0].ruleId, 'MSB1009');
        assert.doesNotMatch(response.output ?? '', /MSB1009/);
        await rm(response.fullLogPath);
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
    await check('diagnostic timeout isolates and removes partial reports', async () => {
        const project = path.join(fixture, 'timeout-report-cleanup');
        const cliDirectory = path.join(project, 'node_modules', 'playwright');
        const marker = path.join(project, 'report-paths.txt');
        const mode = path.join(project, 'mode.txt');
        await mkdir(cliDirectory, { recursive: true });
        const script = `const fs=require('node:fs'); const report=process.env.PLAYWRIGHT_JSON_OUTPUT_NAME; fs.appendFileSync(${JSON.stringify(marker)}, report+'\\n'); if(fs.readFileSync(${JSON.stringify(mode)},'utf8').trim()==='partial'){ fs.writeFileSync(report, '{"suites":['); setInterval(()=>{},1000); } else { fs.writeFileSync(report, JSON.stringify({suites:[],stats:{duration:1}})); }`;
        await writeFile(path.join(cliDirectory, 'cli.js'), script);
        await writeFile(mode, 'partial');

        const timedOut = await tool('run_playwright', { workingDirectory: project, timeoutSeconds: 1 });
        assert.equal(timedOut.status, 'timeout', JSON.stringify(timedOut));
        const firstPath = (await readFile(marker, 'utf8')).trim().split(/\r?\n/).at(0);
        await assert.rejects(stat(path.dirname(firstPath)), { code: 'ENOENT' });
        await rm(timedOut.fullLogPath);

        await writeFile(mode, 'complete');
        const completed = await tool('run_playwright', { workingDirectory: project });
        assert.equal(completed.status, 'no_tests', JSON.stringify(completed));
        const paths = (await readFile(marker, 'utf8')).trim().split(/\r?\n/);
        assert.equal(paths.length, 2);
        assert.notEqual(paths[0], paths[1]);
        await assert.rejects(stat(path.dirname(paths[1])), { code: 'ENOENT' });
        await rm(completed.fullLogPath);
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
    await check('trace text, uncaught frames and relative paths', async () => {
        const zipPath = path.join(fixture, 'trace-colors.zip');
        const color = text => `\u001b[31m${text}\u001b[0m`;
        const trace = [
            { type: 'action', callId: 'fail', apiName: color('expect.visible'), error: { message: color('Assertion failed') } },
            { type: 'error', message: 'Assertion failed', stack: [{ file: 'duplicate.js', line: 1, column: 1 }] },
            { type: 'error', message: color('Uncaught failure'), stack: [
                { function: color('userCode'), file: 'sample.spec.ts', line: 8, column: 3 },
                { function: 'internal', file: 'node_modules/playwright/lib/runner.js', line: 1, column: 2 }
            ] },
            { type: 'error', message: 'Other failure', stack: `${color('Error: Other failure')}\n    at user (sample.spec.ts:12:2)\n    at internal (node_modules/playwright/lib/runner.js:1:2)` },
            { type: 'console', messageType: 'error', text: color('Console failure') }
        ].map(value => JSON.stringify(value)).join('\n');
        await call(`Add-Type -AssemblyName System.IO.Compression; $z = [System.IO.Compression.ZipArchive]::new([System.IO.File]::Create(${literal(zipPath)}), [System.IO.Compression.ZipArchiveMode]::Create); try { $writer = [System.IO.StreamWriter]::new($z.CreateEntry('0.trace').Open()); $writer.Write(${literal(trace)}); $writer.Dispose() } finally { $z.Dispose() }`, { workingDirectory: fixture });
        const inspected = await tool('inspect_playwright_trace', { tracePath: 'trace-colors.zip', workingDirectory: fixture });
        assert.equal(inspected.status, 'inspected', JSON.stringify(inspected));
        assert.equal(await realpath(inspected.tracePath), await realpath(zipPath));
        assert.equal(inspected.failedAction.apiName, 'expect.visible');
        assert.equal(inspected.failedAction.error, 'Assertion failed');
        assert.equal(inspected.consoleErrors[0].text, 'Console failure');
        assert.doesNotMatch(JSON.stringify(inspected), /\\u001b|\u001b/i);
        assert.deepEqual(inspected.uncaughtErrors.map(error => error.message), ['Uncaught failure', 'Other failure']);
        assert.ok(Array.isArray(inspected.uncaughtErrors[0].stack));
        assert.match(inspected.uncaughtErrors[0].stack.join('\n'), /sample\.spec\.ts:8:3/);
        assert.ok(inspected.uncaughtErrors[0].stack.every(frame => frame === frame.trim() && !frame.includes('node_modules/playwright')));
        assert.ok(Array.isArray(inspected.uncaughtErrors[1].stack));
        assert.ok(inspected.uncaughtErrors[1].stack.some(frame => frame.includes('sample.spec.ts:12:2')));
        assert.ok(inspected.uncaughtErrors[1].stack.every(frame => frame === frame.trim() && !frame.includes('node_modules/playwright')));
        const missing = await tool('inspect_playwright_trace', { tracePath: 'missing.zip', workingDirectory: fixture });
        assert.equal(missing.status, 'error');
        assert.equal(path.join(await realpath(path.dirname(missing.tracePath)), path.basename(missing.tracePath)), path.join(await realpath(fixture), 'missing.zip'));
        assert.ok(missing.errors.some(error => error.includes('Trace file not found') && error.includes('missing.zip')), JSON.stringify(missing));
        const relative = await tool('inspect_playwright_trace', { tracePath: path.relative(process.cwd(), zipPath) });
        assert.equal(relative.status, 'inspected', JSON.stringify(relative));
        assert.equal(await realpath(relative.tracePath), await realpath(zipPath));

        const longMessage = 'Long assertion ' + 'x'.repeat(600);
        const longTrace = [
            { type: 'action', callId: 'long', apiName: 'expect', error: { message: color(longMessage) } },
            { type: 'error', message: longMessage },
            { type: 'error', message: `${longMessage} distinct` }
        ].map(value => JSON.stringify(value)).join('\n');
        const longZip = path.join(fixture, 'trace-long.zip');
        await call(`Add-Type -AssemblyName System.IO.Compression; $z = [System.IO.Compression.ZipArchive]::new([System.IO.File]::Create(${literal(longZip)}), [System.IO.Compression.ZipArchiveMode]::Create); try { $writer = [System.IO.StreamWriter]::new($z.CreateEntry('0.trace').Open()); $writer.Write(${literal(longTrace)}); $writer.Dispose() } finally { $z.Dispose() }`, { workingDirectory: fixture });
        const longResult = await tool('inspect_playwright_trace', { tracePath: longZip });
        assert.equal(longResult.uncaughtErrors.length, 1, JSON.stringify(longResult));
    });
    if (process.env.INAZUMA_REAL_TRACE) {
        await check('real Chrome trace error and DOM', async () => {
            const inspected = await tool('inspect_playwright_trace', { tracePath: process.env.INAZUMA_REAL_TRACE, maxOutputChars: 5000 });
            assert.equal(inspected.status, 'inspected', JSON.stringify(inspected));
            assert.ok(inspected.failedAction?.error, JSON.stringify(inspected));
            assert.ok(inspected.latestDomSnapshot?.includes('<BUTTON'), JSON.stringify(inspected));
            assert.ok(inspected.consoleErrors.length > 0, JSON.stringify(inspected));
            assert.doesNotMatch(JSON.stringify(inspected), /\\u001b|\u001b/i);
            assert.ok(inspected.uncaughtErrors.every(error => error.message !== inspected.failedAction?.error), JSON.stringify(inspected));
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
    if (server.exitCode === null) {
        const exited = new Promise(resolve => server.once('exit', resolve));
        server.kill();
        await exited;
    }
    await rm(fixture, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 });
}

