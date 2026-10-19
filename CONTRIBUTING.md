# Contributing to Sol

Contributions should make one compiler behavior easier to specify, verify, or maintain.
Sol is at an early frontend stage; check the [current status](docs/status.md) before relying on an
unimplemented compiler stage.

## Set up a checkout

Use Windows and PowerShell 7. Install the exact .NET SDK selected by `global.json`
(currently 10.0.402). Managed frontend work does not require LLVM, C++/CLI, or the native launcher.
Native launcher and packaging work additionally require Visual Studio C++ build tools, a Windows SDK,
and an installed MSVC v143 or v145 toolset with C++20 support.

```powershell
git clone https://github.com/OpenDot-Project/sol.git
Set-Location sol
git switch -c fix/describe-the-change
dotnet --version
dotnet restore Sol.slnx --locked-mode
dotnet build Sol.slnx --no-restore --configuration Debug
dotnet test --solution Sol.slnx --no-restore --configuration Debug
```

Read the [development guide](docs/development.md) for Release coverage, process smoke tests,
native builds, packaging, and documentation validation.

## Make a change

1. Identify the observable behavior and its error cases. For language changes, describe syntax and
   semantics separately; parser acceptance does not establish type safety or execution behavior.
2. Keep the change within the owning project. Frontend libraries must work without loading LLVM.
3. Add focused regression coverage for compiler behavior: a positive example and relevant negative,
   recovery, Unicode, lifetime, or cancellation cases. Documentation-only edits need link, example,
   and DocFX validation rather than artificial unit tests.
4. Run the relevant local checks. Describe checks you actually ran and any remaining limitations.
5. Update user-facing documentation and `CHANGELOG.md` under `Unreleased` when behavior changes.
6. Open a pull request against `main`. Direct pushes to `main`, force pushes, and protection bypasses
   are outside the contribution workflow. Required CI checks must pass before merging.

Keep commit messages, pull request descriptions, documentation, and source comments in en-US.
Use a PR title that describes the resulting behavior. The body should explain the problem, the
change, validation, and material limitations. Avoid claiming that a compiler stage is complete when
only its parser or serializer exists. Maintainers use squash merges and linear history.

## Implementation conventions

- Managed code: C# 14, nullable reference types, explicit imports, file-scoped namespaces,
  `OpenDot.Sol.*` names, and the repository's `.editorconfig`. Warnings are errors.
- Native code: C++20, `OpenDot::Sol::...` namespaces, Microsoft formatting from `.clang-format`,
  RAII, explicit ownership, and checked Windows API failures. Upstream code retains its own style.
- Build graph: MSBuild. Repository scripts and workflow commands: PowerShell. Do not introduce
  a second scripting stack for repository automation.
- Runtime distribution: framework-dependent managed payload plus a private .NET 10 runtime added
  by our packaging step. No NativeAOT, SDK self-contained publish, or single-file publishing.
- Native interop: add a C++/CLI library only when an actual native library integration needs it;
  do not add an empty bridge to managed-only frontend work.
- Dependencies: keep the production surface small. CLI parsing is first-party. Proposed package
  additions need a clear purpose, license review, an exact version, and committed lockfile updates.

After an intentional dependency change, run `dotnet restore Sol.slnx` to regenerate the affected
lockfiles, inspect their diff, then confirm `dotnet restore Sol.slnx --locked-mode` succeeds.
Do not update unrelated packages to fix a local restore problem.

## Review and validation

CI uses Windows 2025 exclusively. Separate workflows cover Debug/Release regression tests,
coverage upload, DocFX, CodeQL for C# and native C++, actionlint, zizmor, OSV, and component packaging.
Codecov coverage is additional evidence; it does not replace assertions about correctness.
Do not suppress a failure, weaken a required check, or label a skipped capability as tested.

Seeded arbitrary-byte tests check termination and invariants. They are not evidence of native
coverage-guided fuzzing or sanitizer coverage. Add those harnesses with the native components they test.

Generated `bin`, `obj`, `out`, local SDKs, editor state, credentials, and private working notes must
not be committed or uploaded. Check `git diff --cached --check` and staged filenames before committing.
Do not add logs containing tokens, environment dumps, or confidential source code.

## Report a problem

For a reproducible compiler issue, use the repository's
[issue tracker](https://github.com/OpenDot-Project/sol/issues) and include:

- `solc --version --verbose` output and the exact command line.
- A minimal source example, expected behavior, actual diagnostics, and exit code.
- Windows version and whether you used a release component or a source build.

Remove credentials and confidential code before sharing a reproduction. For a security-sensitive
report, email [opendotproject.opendot@gmail.com](mailto:opendotproject.opendot@gmail.com)
instead of posting exploit details publicly. No response-time guarantee is currently published.

Contributions are distributed under the repository's **Apache-2.0 WITH LLVM-exception** license.
Preserve existing copyright, SPDX, and third-party attribution notices.
