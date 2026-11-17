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
        if (typeof entry?.tool !== 'string' || !(entry.sourceBytes > 0) || !(entry.responseBytes >= 0)) continue;
        since ??= entry.time;
        const item = tools.get(entry.tool) ?? { tool: entry.tool, calls: 0, sourceBytes: 0, responseBytes: 0 };
        item.calls++;
        item.sourceBytes += entry.sourceBytes;
        item.responseBytes += entry.responseBytes;
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
        item.text = `$(zap) ${savedPercent(summary.total)}% saved`;
        item.tooltip = `Zuma MCP read ${formatBytes(summary.total.sourceBytes)} of files and logs and returned ${formatBytes(summary.total.responseBytes)} in ${summary.total.calls} tool calls${sinceText()}. Click for the breakdown.`;
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
                description: `${savedPercent(row)}% saved`,
                detail: `${row.calls} calls: read ${formatBytes(row.sourceBytes)}, returned ${formatBytes(row.responseBytes)}`
            });
            await vscode.window.showQuickPick(
                [toPick('Total', summary.total), ...summary.rows.map(row => toPick(row.tool, row))],
                { title: `Zuma MCP savings${sinceText()}`, placeHolder: 'Size of the files and logs each tool read, compared with the size of its response' });
        }));
}

function activate(context) {
    const executable = resolveExecutable(context.extensionPath);
    const changes = new vscode.EventEmitter();
    const provider = {
        onDidChangeMcpServerDefinitions: changes.event,
        provideMcpServerDefinitions() {
            if (!executable) return [];
            const pwshPath = vscode.workspace.getConfiguration('inazumaPwsh').get('pwshPath', '').trim();
            const environment = pwshPath ? { INAZUMA_PWSH_PATH: pwshPath } : undefined;
            const definition = new vscode.McpStdioServerDefinition(
                'Zuma', executable, ['--parent-pid', String(process.pid)], environment, version);
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
            if (event.affectsConfiguration('inazumaPwsh.pwshPath')) changes.fire();
        }));
    registerSavings(context);
    return provider;
}

module.exports = { activate, resolveExecutable, summarizeUsage };