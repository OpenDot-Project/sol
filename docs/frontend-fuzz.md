<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Frontend fuzz corpus

The checked-in `fuzz/corpus/frontend` corpus contains exactly **20,000 distinct source byte
sequences**. Forty JSONL shards hold 500 records each. SHA-256 uniqueness is checked over
decoded source bytes, not case names, JSON records, comments added for identification or
the number of times an input was executed. The manifest binds each shard's bytes and count.

This is a reproducible generated seed corpus and deterministic replay harness. It is not
coverage-guided fuzzing, an exhaustive language test, native sanitizer coverage, or proof
that 20,000 distinct execution paths were reached. GC stress seeds and specification examples
are separate test inventories and are not included in this count.

## Inputs

Each family contains 1,000 inputs:

| Families | Main variations |
|---|---|
| arithmetic, control-flow, declarations | Numeric operands, grouped parameters, branches, partial objects and bindings. |
| generic-types, lambdas, arguments | Nested/nullable type syntax, named/positional lambdas, named/default call arguments. |
| modules, properties | Logical module names independent of paths, imports, constructors and accessors. |
| numeric-boundaries, string-escapes | Number bases, separators, exponents, Unicode escapes and malformed literals. |
| trivia, unicode-newlines | Nested comments, whitespace, CR/LF/CRLF, multibyte strings and non-BMP scalars. |
| truncation, token-mutation, delimiter-mutation, unterminated | Recovery at different source positions, missing delimiters, invalid operators and unfinished tokens. |
| invalid-utf8, arbitrary-bytes | Invalid encodings, NUL/control bytes, empty input and deterministic arbitrary byte strings. |
| nesting, operators | Depth-limit recovery, expression and operator combinations. |

The `arithmetic`, `arguments` and `modules` families must parse without errors. The
`invalid-utf8` family must produce errors. Other families may be accepted or diagnosed;
they test safety and repeatability, including syntax that is not fully implemented. Their
presence is not a claim that every generated construct is a valid Sol program.

Each record contains `id`, `family`, `expectation`, `sha256` and `source_base64`. Base64
preserves malformed UTF-8 without replacement decoding. Inputs are limited to 8 KiB;
unknown, missing or duplicate JSON properties, mismatched identities, invalid digests and
noncanonical base64 are rejected. Verification checks the full 20,000-source inventory
even when replay selects a single shard or case. Corpus records and the manifest receive
the repository's copyright/license metadata through `REUSE.toml`.

## Running and reproducing

From the repository root, with the pinned .NET 10 SDK:

```powershell
./eng/new-frontend-fuzz-corpus.ps1 -Check
./eng/test-fuzz.ps1 -Target frontend
./eng/test-fuzz.ps1 -Target frontend -Shard 31
./eng/test-fuzz.ps1 -Target frontend -CaseId case-15500
```

Generation is PowerShell; the lexer/parser worker is a separate managed .NET process with
no native LLVM dependency. `-Check` regenerates the corpus in a temporary output directory
and compares every file byte for byte. The generator uses SHA-256-derived deterministic
values and stores a normalized generator-source digest in the manifest. Changing generation
rules requires regenerating and reviewing the data and manifest together. Generation refuses
to overwrite an existing output directory.

For each input, the worker verifies contiguous token/trivia byte coverage, EOF and diagnostic
bounds, CST spans/root extent, bounded node expansion, repeatable CST/tokens/diagnostics and
the explicit acceptance/rejection contract. It parses each input twice; repeated execution
does not increase corpus size. Case identity and source hash are flushed to a journal before
parsing, so the final journal entry identifies a crash or stalled case.

The default runner processes shards sequentially in separate workers. Each input has a
two-second cooperative cancellation budget. Each worker has a 60-second wall-clock limit
and a 512 MiB working-set limit sampled every 100 ms. A stuck worker is terminated along
with its child process tree. Sampling is not an OS-enforced instantaneous memory ceiling;
cooperative cancellation is not a per-input hard process timeout. Shard process termination
and the CI job timeout provide additional containment.

Reports, stdout/stderr and journals remain under a unique `out/fuzz/frontend/` directory.
An invariant failure, timeout, missing/malformed report, incomplete count or worker failure
fails the run. Use the journal's last case ID with `-CaseId` to reproduce. New failures need
triage and a minimized regression; adding unrelated inputs does not resolve them.

The separate `frontend-fuzz.yml` workflow regenerates/verifies and replays these 20,000 inputs
on `windows-2025`, preserving only the allowed fuzz report directory. Its corpus-validator
self-test also rejects corrupt metadata before replay. Compiler outputs are never executed
by this frontend harness, and the worker receives no publication credentials.

## Coverage-guided frontend campaigns

`coverage-fuzz.yml` also runs a libFuzzer campaign over raw Sol source bytes. The managed target
uses SharpFuzz 2.3.0 to instrument the CST lexer/parser assemblies; the harness and .NET runtime
are not counted as target coverage. Each input passes the same byte-preservation, EOF, CST-span,
diagnostic-bound, deterministic-repeat, and token/trivia oracle used by corpus replay. Invalid
UTF-8 and syntax diagnostics are valid outcomes. An invariant failure or unexpected exception
is a finding.

The PR smoke campaign uses 400 seeds selected evenly from the 20 grammar/trivia/invalid-byte
families and runs each selected target for 20 seconds. The daily campaign starts from 2,000
seeds (100 per family) and runs each target for 300 seconds. Both enforce an 8 KiB maximum
input, a 10-second per-input timeout, and a 1 GiB process-tree working-set ceiling; the LLVM
engine receives the matching RSS limit as well. Runs use the Sol token dictionary and value
profiling. These budgets are per target, not a claim about distinct code paths.

The daily schedule is 02:00 UTC on the single `windows-2025` runner. A crash, timeout, OOM, zero
executions, missing instrumented counters, or zero final libFuzzer features fails the campaign.
The workflow uploads the campaign corpus, logs,
summary, and minimized reproducer files under one run-specific artifact for 14 days. New inputs
stay in the campaign artifact until a minimized, deterministic regression is reviewed; the
workflow never commits or pushes corpus files. See the [campaign inventory](fuzz-campaigns.md)
for commands and runtime/backend/GC targets.

This is one target in the [100,000-input corpus inventory](fuzz-corpora.md). All five corpora
are checked for cross-target byte uniqueness before a selected target is replayed.
