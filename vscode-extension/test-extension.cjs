const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { createHash } = require('node:crypto');

async function main() {
    const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, 'package.json'), 'utf8'));
    let provider;
    let configuredPath = '';
    const configurationListeners = [];
    let changeCount = 0;
    const configurationChanged = event => configurationListeners.forEach(listener => listener(event));
    let savingsItem;
    let executionGateItem;
    const commandHandlers = new Map();
    let usageChanged;
    let gateStateChanged;
    let usageText = '';
    let gateStateText = '';
    let picks;
    const disposable = { dispose() {} };
    const api = {
        EventEmitter: class {
            constructor() { this.event = () => disposable; }
            fire() { changeCount++; }
            dispose() {}
        },
        workspace: {
            getConfiguration(section) {
                assert.equal(section, 'inazumaPwsh');
                return {
                    get(key, fallback) {
                        const values = {
                            pwshPath: configuredPath,
                            'executionGuard.enabled': true,
                            'executionGuard.mode': 'manual',
                            'executionGuard.repeatFailureThreshold': 3,
                            'executionGuard.historySeconds': 120,
                            'executionGuard.skipRedPhase': false
                        };
                        return Object.prototype.hasOwnProperty.call(values, key) ? values[key] : fallback;
                    },
                    async update() {}
                };
            },
            onDidChangeConfiguration(listener) { configurationListeners.push(listener); return disposable; },
            onDidChangeWorkspaceFolders() { return disposable; }
        },
        lm: { registerMcpServerDefinitionProvider(id, value) {
            assert.equal(id, manifest.contributes.mcpServerDefinitionProviders[0].id);
            provider = value;
            return disposable;
        } },
        McpStdioServerDefinition: class {
            constructor(label, command, args, env, version) { Object.assign(this, { label, command, args, env, version }); }
        },
        Uri: { file: value => value },
        StatusBarAlignment: { Right: 2 },
        window: {
            createStatusBarItem(id) {
                const item = { show() {}, hide() {}, dispose() {} };
                if (id === 'inazumaPwsh.savings') savingsItem = item;
                else if (id === 'inazumaPwsh.executionGate') executionGateItem = item;
                else assert.fail(`unexpected status item ${id}`);
                return item;
            },
            showQuickPick(items) { picks = items; },
            showInformationMessage() {}
        },
        commands: { registerCommand(id, handler) {
            commandHandlers.set(id, handler);
            return disposable;
        } }
    };
    const context = { extensionPath: __dirname, subscriptions: [] };
    const sandbox = { module: { exports: {} }, process, require: name => {
        if (name === 'vscode') return api;
        if (name === 'node:fs') return { ...fs, watchFile: (file, options, listener) => {
            if (String(file).endsWith('execution-gate.json')) gateStateChanged = listener;
            else usageChanged = listener;
        }, unwatchFile() {} };
        if (name === 'node:fs/promises') return { ...require(name), readFile: async file => String(file).endsWith('execution-gate.json') ? gateStateText : usageText };
        return require(name);
    } };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, 'extension.js'), 'utf8'), sandbox);
    sandbox.module.exports.activate(context);
    assert.equal(context.subscriptions[0], disposable);
    const [definition] = provider.provideMcpServerDefinitions();
    assert.equal(definition.label, 'Zuma');
    assert.equal(definition.version, manifest.version);
    assert.equal(definition.args.join(' '), `--parent-pid ${process.pid}`);
    assert.equal(JSON.stringify(definition.env), JSON.stringify({
        ZUMA_EXECUTION_GATE_ENABLED: 'true',
        ZUMA_EXECUTION_GATE_MODE: 'manual',
        ZUMA_EXECUTION_GATE_THRESHOLD: '3',
        ZUMA_EXECUTION_GATE_HISTORY_SECONDS: '120',
        ZUMA_EXECUTION_GATE_SKIP_RED_PHASE: 'false',
        ZUMA_EXECUTION_GATE_GENERATION: '0',
        ZUMA_HANDOFF_ENABLED: 'false'
    }));
    assert.equal(definition.cwd, path.dirname(definition.command));
    assert.equal(await provider.resolveMcpServerDefinition(definition), definition);
    assert.ok(fs.statSync(definition.command).size > 0);
    assert.ok(fs.statSync(path.join(__dirname, manifest.icon)).size > 0);
    assert.equal(manifest.capabilities.untrustedWorkspaces.supported, false);
    assert.equal(manifest.version, '1.0.1');
    assert.equal(manifest.displayName, 'Zuma MCP - Copilot Chat Log & Context Optimizer');
    assert.equal(manifest.publisher, 'ShunsukeF');
    assert.equal(manifest.repository?.url, 'https://github.com/furukawa658/inazuma-pwsh.git');
    assert.equal(manifest.bugs?.url, 'https://github.com/furukawa658/inazuma-pwsh/issues');
    assert.equal(manifest.homepage, 'https://github.com/furukawa658/inazuma-pwsh#readme');
    assert.ok(manifest.files.includes('THIRD-PARTY-NOTICES.txt'));
    assert.ok(fs.statSync(path.join(__dirname, 'THIRD-PARTY-NOTICES.txt')).size > 0);
    assert.ok(manifest.scripts.package.includes('zuma-mcp-1.0.1-win32-x64.vsix'));
    assert.ok(!manifest.scripts.package.includes('TEST-ONLY'));

    // Platform mapping
    const { resolveExecutable } = sandbox.module.exports;
    const join = (...parts) => path.join(...parts);
    assert.equal(resolveExecutable('/x', 'win32', 'x64'), join('/x', 'server', 'win-x64', 'InazumaPwsh.exe'));
    assert.equal(resolveExecutable('/x', 'linux', 'x64'), join('/x', 'server', 'linux-x64', 'InazumaPwsh'));
    assert.equal(resolveExecutable('/x', 'linux', 'arm64'), join('/x', 'server', 'linux-arm64', 'InazumaPwsh'));
    assert.equal(resolveExecutable('/x', 'darwin', 'x64'), join('/x', 'server', 'osx-x64', 'InazumaPwsh'));
    assert.equal(resolveExecutable('/x', 'darwin', 'arm64'), join('/x', 'server', 'osx-arm64', 'InazumaPwsh'));
    assert.equal(resolveExecutable('/x', 'win32', 'arm64'), undefined);
    assert.equal(resolveExecutable('/x', 'freebsd', 'x64'), undefined);

    // Env and configuration tests
    assert.equal(provider.provideMcpServerDefinitions()[0].env.INAZUMA_PWSH_PATH, undefined);
    configuredPath = '  C:\\Program Files\\PowerShell\\7\\pwsh.exe  ';
    assert.equal(provider.provideMcpServerDefinitions()[0].env.INAZUMA_PWSH_PATH, 'C:\\Program Files\\PowerShell\\7\\pwsh.exe');
    configurationChanged({ affectsConfiguration: key => key === 'inazumaPwsh.pwshPath' });
    assert.equal(changeCount, 1);
    configurationChanged({ affectsConfiguration: () => false });
    assert.equal(changeCount, 1);
    configuredPath = 'D:\\Tools\\pwsh\\pwsh.exe';
    assert.equal(provider.provideMcpServerDefinitions()[0].env.INAZUMA_PWSH_PATH, 'D:\\Tools\\pwsh\\pwsh.exe');
    configurationChanged({ affectsConfiguration: key => key === 'inazumaPwsh.pwshPath' });
    assert.equal(changeCount, 2);
    configuredPath = '   ';
    assert.equal(provider.provideMcpServerDefinitions()[0].env.INAZUMA_PWSH_PATH, undefined);
    configurationChanged({ affectsConfiguration: key => key === 'inazumaPwsh.pwshPath' });
    assert.equal(changeCount, 3);

    // Savings summary
    const usage = sandbox.module.exports.summarizeUsage([
        '{"time":"2026-11-17T00:00:00Z","tool":"peek_file","sourceBytes":1000,"responseBytes":100}',
        '{"time":"2026-11-17T00:01:00Z","tool":"summarize_log","sourceBytes":9000,"responseBytes":100}',
        '{"time":"2026-11-17T00:02:00Z","tool":"peek_file","sourceBytes":1000,"responseBytes":300}',
        '{"tool":"old","sourceBytes":5,"responseChars":1}',
        'not json', ''
    ].join('\n'));
    assert.equal(usage.since, '2026-11-17T00:00:00Z');
    assert.equal(JSON.stringify(usage.total), JSON.stringify({ calls: 3, sourceBytes: 11000, responseBytes: 500 }));
    assert.equal(usage.rows.map(row => `${row.tool}:${row.calls}`).join(','), 'summarize_log:1,peek_file:2');
    const mixedUsage = sandbox.module.exports.summarizeUsage([
        '{"time":"2026-11-17T00:00:00Z","tool":"peek_file","sourceBytes":1000,"responseBytes":100}',
        '{"time":"2026-11-17T00:01:00Z","tool":"execute_powershell","countOnly":true}',
        '{"time":"2026-11-17T00:02:00Z","tool":"search_files","countOnly":true}',
        '{"tool":"old","sourceBytes":5,"responseChars":1}'
    ].join('\n'));
    assert.equal(JSON.stringify(mixedUsage.total), JSON.stringify({ calls: 3, sourceBytes: 1000, responseBytes: 100 }));
    assert.equal(mixedUsage.rows.find(row => row.tool === 'execute_powershell').calls, 1);
    assert.equal(mixedUsage.rows.find(row => row.tool === 'search_files').calls, 1);
    const countOnlyUsage = sandbox.module.exports.summarizeUsage('{"tool":"execute_powershell","countOnly":true}');
    assert.equal(JSON.stringify(countOnlyUsage.total), JSON.stringify({ calls: 1, sourceBytes: 0, responseBytes: 0 }));
    usageText = [
        '{"time":"2026-11-17T00:00:00Z","tool":"peek_file","sourceBytes":1000,"responseBytes":100}',
        '{"time":"2026-11-17T00:01:00Z","tool":"execute_powershell","countOnly":true}'
    ].join('\n');
    await usageChanged();
    assert.equal(savingsItem.text, '$(zap) 90% saved | 2 calls');
    await commandHandlers.get('inazumaPwsh.showSavings')();
    assert.equal(picks.find(pick => pick.label === 'execute_powershell').description, 'Savings not measured');
    usageText += '\n{"tool":"search_files","countOnly":true}';
    await usageChanged();
    assert.equal(savingsItem.text, '$(zap) 90% saved | 3 calls');
    gateStateText = JSON.stringify({ mode: 'incremental', tripped: [{ key: 'fixture' }] });
    await gateStateChanged();
    assert.equal(executionGateItem.text, '$(debug-breakpoint-log) Zuma: Breaker tripped');
    assert.ok(commandHandlers.has('inazumaPwsh.selectExecutionGateMode'));
    assert.ok(commandHandlers.has('inazumaPwsh.resetExecutionGate'));
    await commandHandlers.get('inazumaPwsh.resetExecutionGate')();
    assert.equal(provider.provideMcpServerDefinitions()[0].env.ZUMA_EXECUTION_GATE_GENERATION, '1');
    for (const subscription of context.subscriptions) subscription.dispose();
    console.log('EXTENSION_PROVIDER_TEST_PASSED');
}

exports.run = async function run() {
    const vscode = require('vscode');
    const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, 'package.json'), 'utf8'));
    const extension = vscode.extensions.getExtension(`${manifest.publisher}.${manifest.name}`);
    assert.ok(extension, 'extension must be installed or under development');
    const provider = await extension.activate();
    const [definition] = provider.provideMcpServerDefinitions();
    assert.ok(definition instanceof vscode.McpStdioServerDefinition);
    assert.equal(definition.label, 'InazumaPwsh');
    assert.equal(definition.args.join(' '), `--parent-pid ${process.pid}`);
    assert.equal(await provider.resolveMcpServerDefinition(definition), definition);
    assert.ok(fs.statSync(definition.command).size > 0);
    const publishedExecutable = process.env.INAZUMA_TEST_EXPECTED_EXE ?? path.join(__dirname, '..', 'artifacts', `win-x64-${manifest.version}`, 'InazumaPwsh.exe');
    const hash = file => createHash('sha256').update(fs.readFileSync(file)).digest('hex');
    assert.equal(hash(definition.command), hash(publishedExecutable), 'bundled executable must match validated release');
    console.log('REAL_EXTENSION_HOST_TEST_PASSED');
};

if (require.main === module) main().catch(error => { console.error(error); process.exitCode = 1; });