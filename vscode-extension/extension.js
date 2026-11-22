const vscode = require('vscode');
const { watchFile, unwatchFile } = require('node:fs');
const { access, chmod, readFile } = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { version } = require('./package.json');

const runtimeIds = {
    'win32-x64': 'win-x64',
    'linux-x64': 'linux-x64',
    'linux-arm64': 'linux-arm64',
    'darwin-x64': 'osx-x64',
    'darwin-arm64': 'osx-arm64'
};

function resolveExecutable(extensionPath, platform = process.platform, arch = process.arch) {
    const runtimeId = runtimeIds[`${platform}-${arch}`];
    if (!runtimeId) return undefined;
    return path.join(extensionPath, 'server', runtimeId, platform === 'win32' ? 'InazumaPwsh.exe' : 'InazumaPwsh');
}

function summarizeUsage(text) {
    const tools = new Map();
    let since;
    for (const line of text.split('\n')) {
        let entry;
        try { entry = JSON.parse(line); } catch { continue; }
        const measured = entry?.sourceBytes > 0 && entry?.responseBytes >= 0;
        if (typeof entry?.tool !== 'string' || (!measured && entry.countOnly !== true)) continue;
        since ??= entry.time;
        const item = tools.get(entry.tool) ?? { tool: entry.tool, calls: 0, sourceBytes: 0, responseBytes: 0 };
        item.calls++;
        if (measured) {
            item.sourceBytes += entry.sourceBytes;
            item.responseBytes += entry.responseBytes;
        }
        tools.set(entry.tool, item);
    }
    const rows = [...tools.values()].sort((a, b) => (b.sourceBytes - b.responseBytes) - (a.sourceBytes - a.responseBytes));
    const total = { calls: 0, sourceBytes: 0, responseBytes: 0 };
    for (const row of rows) {
        total.calls += row.calls;
        total.sourceBytes += row.sourceBytes;
        total.responseBytes += row.responseBytes;
    }
    return { since, total, rows };
}

const savedPercent = item => item.sourceBytes > 0 ? Math.round((1 - item.responseBytes / item.sourceBytes) * 100) : 0;

function formatBytes(bytes) {
    if (bytes < 1024) return `${bytes} B`;
    return bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(1)} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

function registerExecutionGate(context, changes, restartServer) {
    const statePath = path.join(os.tmpdir(), 'inazuma-pwsh-logs', 'execution-gate.json');
    const item = vscode.window.createStatusBarItem('inazumaPwsh.executionGate', vscode.StatusBarAlignment.Right, 1);
    item.name = 'Zuma MCP execution gate';
    item.command = 'inazumaPwsh.selectExecutionGateMode';
    const configuration = () => vscode.workspace.getConfiguration('inazumaPwsh');
    const refresh = async () => {
        const enabled = configuration().get('executionGuard.enabled', true);
        const mode = configuration().get('executionGuard.mode', 'manual');
        let tripped = false;
        try {
            const state = JSON.parse(await readFile(statePath, 'utf8'));
            tripped = Boolean(state.tripped?.length);
        } catch { }
        item.text = !enabled ? '$(circle-slash) Zuma: Guard off'
            : tripped ? '$(debug-breakpoint-log) Zuma: Breaker tripped'
            : `$(shield) Zuma: ${mode[0].toUpperCase()}${mode.slice(1)}`;
        item.tooltip = tripped
            ? 'Zuma stopped a repeated-failure loop. Click to change mode or use Reset Execution Gate.'
            : `Zuma execution gate mode: ${mode}. Click to change the workspace mode.`;
        item.show();
    };
    watchFile(statePath, { interval: 1000 }, refresh);
    refresh();
    context.subscriptions.push(
        item,
        { dispose: () => unwatchFile(statePath, refresh) },
        vscode.commands.registerCommand('inazumaPwsh.selectExecutionGateMode', async () => {
            const selected = await vscode.window.showQuickPick([
                { label: 'manual', description: 'Keep normal test execution; repeat-failure breaker remains active' },
                { label: 'incremental', description: 'Block unscoped full tests; use affected or specific tests' },
                { label: 'milestone', description: 'Allow the full regression suite' }
            ], { title: 'Zuma MCP execution gate mode', placeHolder: 'Choose the workspace mode' });
            if (!selected) return;
            await configuration().update('executionGuard.mode', selected.label, vscode.ConfigurationTarget.Workspace);
            changes.fire();
            await refresh();
        }),
        vscode.commands.registerCommand('inazumaPwsh.resetExecutionGate', async () => {
            restartServer();
            changes.fire();
            await refresh();
            vscode.window.showInformationMessage('Zuma MCP execution gate reset requested. The next server definition will start with empty failure history.');
        }),
        vscode.workspace.onDidChangeConfiguration(event => {
            if (event.affectsConfiguration('inazumaPwsh.executionGuard')) refresh();
        }));
}

function registerSavings(context) {
    const usagePath = path.join(os.tmpdir(), 'inazuma-pwsh-logs', 'usage.jsonl');
    const item = vscode.window.createStatusBarItem('inazumaPwsh.savings', vscode.StatusBarAlignment.Right, 0);
    item.name = 'Zuma MCP savings';
    item.command = 'inazumaPwsh.showSavings';
    let summary = summarizeUsage('');
    const sinceText = () => summary.since ? ` since ${new Date(summary.since).toLocaleString()}` : '';
    const refresh = async () => {
        let text = '';
        try { text = await readFile(usagePath, 'utf8'); } catch { }
        summary = summarizeUsage(text);
        if (summary.total.calls === 0) { item.hide(); return; }
        const savings = summary.total.sourceBytes > 0 ? `${savedPercent(summary.total)}% saved | ` : '';
        item.text = `$(zap) ${savings}${summary.total.calls} calls`;
        item.tooltip = `Zuma MCP recorded ${summary.total.calls} tool calls${sinceText()}. For calls with measured savings, it read ${formatBytes(summary.total.sourceBytes)} and returned ${formatBytes(summary.total.responseBytes)}. Click for the breakdown.`;
        item.show();
    };
    watchFile(usagePath, { interval: 2000 }, refresh);
    refresh();
    context.subscriptions.push(
        item,
        { dispose: () => unwatchFile(usagePath, refresh) },
        vscode.commands.registerCommand('inazumaPwsh.showSavings', async () => {
            await refresh();
            if (summary.total.calls === 0) {
                vscode.window.showInformationMessage('Zuma MCP has not recorded any tool calls yet.');
                return;
            }
            const toPick = (label, row) => ({
                label,
                description: row.sourceBytes > 0 ? `${savedPercent(row)}% saved` : 'Savings not measured',
                detail: row.sourceBytes > 0
                    ? `${row.calls} calls; measured: read ${formatBytes(row.sourceBytes)}, returned ${formatBytes(row.responseBytes)}`
                    : `${row.calls} calls`
            });
            await vscode.window.showQuickPick(
                [toPick('Total', summary.total), ...summary.rows.map(row => toPick(row.tool, row))],
                { title: `Zuma MCP savings${sinceText()}`, placeHolder: 'Size of the files and logs each tool read, compared with the size of its response' });
        }));
}

function activate(context) {
    const executable = resolveExecutable(context.extensionPath);
    let executionGateGeneration = 0;
    const changes = new vscode.EventEmitter();
    const provider = {
        onDidChangeMcpServerDefinitions: changes.event,
        provideMcpServerDefinitions() {
            if (!executable) return [];
            const config = vscode.workspace.getConfiguration('inazumaPwsh');
            const pwshPath = config.get('pwshPath', '').trim();
            const workspaceRoot = vscode.workspace.workspaceFolders?.[0]?.uri;
            const environment = {
                ZUMA_EXECUTION_GATE_ENABLED: String(config.get('executionGuard.enabled', true)),
                ZUMA_EXECUTION_GATE_MODE: config.get('executionGuard.mode', 'manual'),
                ZUMA_EXECUTION_GATE_THRESHOLD: String(config.get('executionGuard.repeatFailureThreshold', 3)),
                ZUMA_EXECUTION_GATE_HISTORY_SECONDS: String(config.get('executionGuard.historySeconds', 120)),
                ZUMA_EXECUTION_GATE_SKIP_RED_PHASE: String(config.get('executionGuard.skipRedPhase', false)),
                ZUMA_EXECUTION_GATE_GENERATION: String(executionGateGeneration)
            };
            if (pwshPath) environment.INAZUMA_PWSH_PATH = pwshPath;
            if (workspaceRoot?.scheme === 'file') environment.ZUMA_WORKSPACE_ROOT = workspaceRoot.fsPath;
            const definition = new vscode.McpStdioServerDefinition(
                'Zuma', executable, ['--parent-pid', String(process.pid)], Object.keys(environment).length ? environment : undefined, version);
            definition.cwd = vscode.Uri.file(path.dirname(executable));
            return [definition];
        },
        async resolveMcpServerDefinition(definition) {
            await access(executable);
            // VSIX packaged on Windows does not keep the Unix executable bit.
            if (process.platform !== 'win32') await chmod(executable, 0o755);
            return definition;
        }
    };
    context.subscriptions.push(
        vscode.lm.registerMcpServerDefinitionProvider('inazuma-pwsh.server', provider),
        changes,
        vscode.workspace.onDidChangeConfiguration(event => {
            if (event.affectsConfiguration('inazumaPwsh.pwshPath') || event.affectsConfiguration('inazumaPwsh.executionGuard')) changes.fire();
        }),
        vscode.workspace.onDidChangeWorkspaceFolders(() => changes.fire()));
    registerSavings(context);
    registerExecutionGate(context, changes, () => { executionGateGeneration++; });
    return provider;
}

module.exports = { activate, resolveExecutable, summarizeUsage };