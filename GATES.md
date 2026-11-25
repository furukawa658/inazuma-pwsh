# Gates: Zuma MCP 0.14 context engine

OWNS: rust/zuma-mcp/**, *.cs, vscode-extension/**, tools/**, README.md, CHANGELOG.md, docs/**, GATES.md

Scope: Add deterministic semantic diff, persistent local context facts, compiled context, query-first read planning, 0.14 documentation, and verified publication.

- [x] G1: Context engine C# build and MCP integration pass
  CHECK: node tools/verify-context-release.mjs core
  EXPECT: CONTEXT_CORE_OK
  EVIDENCE: 2026-11-25, MCP integration 80 passed, CONTEXT_CORE_OK

- [x] G2: Rust tool protocol and packaged routing expose all context tools
  CHECK: node tools/verify-context-release.mjs rust
  EXPECT: CONTEXT_RUST_OK
  EVIDENCE: 2026-11-25, Rust unit 10 and protocol 6 passed, CONTEXT_RUST_OK

- [x] G3: Packaged response and tool-count regressions pass
  CHECK: node tools/verify-context-release.mjs package
  EXPECT: CONTEXT_PACKAGE_OK
  EVIDENCE: 2026-11-25, tools=33, context_engine=true, REAL_SMOKE_OK, PARENT_LIFETIME_OK

- [x] G4: 0.14.0 README keeps only the latest three minor releases and links the full changelog
  CHECK: node tools/verify-context-release.mjs docs
  EXPECT: CONTEXT_DOCS_OK
  EVIDENCE: 2026-11-25, CONTEXT_DOCS_OK

- [x] G5: Version metadata, working tree, and release workflow target 0.14.0
  CHECK: node tools/verify-context-release.mjs release
  EXPECT: CONTEXT_RELEASE_OK
  EVIDENCE: 2026-11-25, version and workflow checks passed, CONTEXT_RELEASE_OK

- [ ] G6: Marketplace 0.14.0 is published for all five platform targets
  EVIDENCE: Pending
