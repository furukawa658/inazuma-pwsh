const vscode = require('vscode');
const { access, chmod } = require('node:fs/promises');
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
                'InazumaPwsh', executable, ['--parent-pid', String(process.pid)], environment, version);
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
    return provider;
}

module.exports = { activate, resolveExecutable };