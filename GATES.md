# Gates: Zuma MCP 0.11 handoff

OWNS: rust/zuma-mcp/**, *.cs, vscode-extension/**, README.md, CHANGELOG.md, docs/**, GATES.md

Scope: Add safe deterministic chat handoff generation, Marketplace messaging, release metadata, and publish verification for Zuma MCP 0.11.0.

- [x] G1: Rust handoff protocol and parser tests pass
  CHECK: node tools/verify-handoff-release.mjs rust
  EXPECT: HANDOFF_TESTS_OK
  EVIDENCE: 6 Rust tests passed and HANDOFF_TESTS_OK was emitted on 2026-11-24

- [x] G2: C# build and extension provider tests pass
  CHECK: node tools/verify-handoff-release.mjs
  EXPECT: HANDOFF_RELEASE_CHECKS_OK
  EVIDENCE: .NET build completed with 0 warnings and 0 errors; extension provider test passed on 2026-11-24

- [x] G3: Packaged MCP server exposes and executes prepare_handoff
  CHECK: node tools/verify-handoff-release.mjs package
  EXPECT: PACKAGE_OK
  EVIDENCE: 0.11.0 win32-x64 VSIX packaged; 23-tool real smoke, cancellation, dotnet build, handoff, and parent lifetime passed on 2026-11-24

- [ ] G4: Marketplace release 0.11.0 is published and its display name is visible
  EVIDENCE: pending
