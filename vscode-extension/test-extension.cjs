const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { createHash } = require('node:crypto');

async function main() {
    const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, 'package.json'), 'utf8'));
    let provider;
    let configuredPath = '';
    let configurationChanged;
    let changeCount = 0;
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
                return { get(key) { assert.equal(key, 'pwshPath'); return configuredPath; } };
            },
            onDidChangeConfiguration(listener) { configurationChanged = listener; return disposable; }
        },
        lm: { registerMcpServerDefinitionProvider(id, value) {
            assert.equal(id, manifest.contributes.mcpServerDefinitionProviders[0].id);
            provider = value;
            return disposable;
        } },
        McpStdioServerDefinition: class {
            constructor(label, command, args, env, version) { Object.assign(this, { label, command, args, env, version }); }
        },
        Uri: { file: value => value }
    };
    const context = { extensionPath: __dirname, subscriptions: [] };
    const sandbox = { module: { exports: {} }, process, require: name => name === 'vscode' ? api : require(name) };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, 'extension.js'), 'utf8'), sandbox);
    sandbox.module.exports.activate(context);
    assert.equal(context.subscriptions[0], disposable);
    const [definition] = provider.provideMcpServerDefinitions();
    assert.equal(definition.label, 'InazumaPwsh');
    assert.equal(definition.version, manifest.version);
    assert.equal(definition.args.join(' '), `--parent-pid ${process.pid}`);
    assert.equal(definition.env, undefined);
    assert.equal(definition.cwd, path.dirname(definition.command));
    assert.equal(await provider.resolveMcpServerDefinition(definition), definition);
    assert.ok(fs.statSync(definition.command).size > 0);
    assert.ok(fs.statSync(path.join(__dirname, manifest.icon)).size > 0);
    assert.equal(manifest.capabilities.untrustedWorkspaces.supported, false);
    assert.equal(manifest.version, '0.3.0');
    assert.equal(manifest.publisher, 'ShunsukeF');
    assert.equal(manifest.repository?.url, 'https://github.com/furukawa658/inazuma-pwsh.git');
    assert.equal(manifest.bugs?.url, 'https://github.com/furukawa658/inazuma-pwsh/issues');
    assert.equal(manifest.homepage, 'https://github.com/furukawa658/inazuma-pwsh#readme');
    assert.ok(manifest.files.includes('THIRD-PARTY-NOTICES.txt'));
    assert.ok(fs.statSync(path.join(__dirname, 'THIRD-PARTY-NOTICES.txt')).size > 0);
    assert.ok(manifest.scripts.package.includes('inazuma-pwsh-0.3.0-win32-x64.vsix'));
    assert.ok(!manifest.scripts.package.includes('TEST-ONLY'));

    // Env and configuration tests
    assert.equal(provider.provideMcpServerDefinitions()[0].env, undefined);
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
    assert.equal(provider.provideMcpServerDefinitions()[0].env, undefined);
    configurationChanged({ affectsConfiguration: key => key === 'inazumaPwsh.pwshPath' });
    assert.equal(changeCount, 3);
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