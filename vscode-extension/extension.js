const vscode = require('vscode');
const { access } = require('node:fs/promises');
const path = require('node:path');
const { version } = require('./package.json');

function activate(context) {
    const executable = path.join(context.extensionPath, 'server', 'win-x64', 'InazumaPwsh.exe');
    const changes = new vscode.EventEmitter();
    const provider = {
        onDidChangeMcpServerDefinitions: changes.event,
        provideMcpServerDefinitions() {
            if (process.platform !== 'win32' || process.arch !== 'x64') return [];
            const pwshPath = vscode.workspace.getConfiguration('inazumaPwsh').get('pwshPath', '').trim();
            const environment = pwshPath ? { INAZUMA_PWSH_PATH: pwshPath } : undefined;
            const definition = new vscode.McpStdioServerDefinition(
                'InazumaPwsh', executable, ['--parent-pid', String(process.pid)], environment, version);
            definition.cwd = vscode.Uri.file(path.dirname(executable));
            return [definition];
        },
        async resolveMcpServerDefinition(definition) {
            await access(executable);
            return definition;
        }
    };
    context.subscriptions.push(
        vscode.lm.registerMcpServerDefinitionProvider('inazuma-pwsh.server', provider),
        changes,
        vscode.workspace.onDidChangeConfiguration(event => {
            if (event.affectsConfiguration('inazumaPwsh.pwshPath')) changes.fire();
        }));
    return provider;
}

module.exports = { activate };