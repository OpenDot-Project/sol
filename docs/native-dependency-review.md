<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Native dependency review

Native package execution tests, source analysis, advisory matching and release qualification
provide different evidence. A passing compiler or GC test does not clear an upstream advisory.
This page records the native dependency review for the experimental 0.0.2 release target.

## Exact inputs

`eng/native-advisories.json` records the source repositories, release tags, annotated tag objects
and dereferenced commits verified on October 23, 2026. LLVM remains pinned to 23.1.3.
The three native libraries are selected by the hash-pinned vcpkg revision in
`eng/llvm-native-dependencies.json`.

| Component | Upstream version | Source commit |
|---|---|---|
| LLVM, including embedded LLD | 23.1.3 | `0d261d1ca552c95a8f007e061c787ac7132fbcbc` |
| zlib | 1.3.2 plus upstream gzip-write fix | `df84af25dc1942490e1d1c899a07619152a46148` |
| zstd | 1.5.7 | `f8745da6ff1ad1e7bab384bd1f9d742439278e99` |
| libxml2 | 2.15.4 | `96498992efa48d52b0e8b83058bd88dbdaf153c1` |

The zlib base tag remains v1.3.2 at `da607da739fa6047df13e66a2af6b8bec7c2a498`; the catalog
separately identifies the fixed source commit and its archive SHA-512. This is not a new
upstream release. Generated vcpkg overlay ports select that exact source and disable libxml2
HTML and XInclude. The downloaded vcpkg checkout remains unchanged. Port revisions and
vcpkg binary ABI hashes are not upstream source versions. libxml2's explicit GitLab mirror
mapping permits OSV records that use its canonical GitLab repository rather than GitHub.
An unrelated repository is not accepted as evidence about a component.

These inputs are not an attestation that every release binary was built from a locally
reproduced source tree. Native package metadata separately records the actual vcpkg ABI
identities and LLVM version. Binary/source provenance and patch accounting remain separate work.

## Native advisory gate

```powershell
./eng/test-native-advisory-validation.ps1
./eng/test-native-advisories.ps1
```

The first command runs offline evidence-validation regressions. The second checks the build
pins and port versions, then queries each native source commit through OSV's
[commit query API](https://google.github.io/osv.dev/post-v1-query/).
The existing OSV-Scanner job continues to scan NuGet lockfiles.

Native repositories must not be represented as GitHub Actions packages. In a qualification
probe, OSV-Scanner 2.7.0 interpreted `pkg:github/...@<commit>` entries in a CycloneDX document
as the GitHub Actions ecosystem. That probe returned no findings for the four projects,
whereas explicit native commit queries returned four records. The probe's clean result is
not native advisory coverage and is not used by this gate.

The script writes a new report under `out/security/native-advisories/`. It includes the Sol
source SHA, query time, catalog digest, query commits, pagination counts and full returned
advisory records. The Windows workflow preserves that report when a query finds an advisory.
The workflow first builds the selected native libraries and qualifies their binary identities.
It requires no credentials. Dependency smoke tests execute the resulting native code.

The evidence validator rejects malformed responses, wrong repositories, missing affected
component evidence, duplicate advisory identities, repeated continuation tokens and pagination
beyond ten pages. Each continuation retains the original source commit. Unknown or failed
queries fail qualification; they are not converted into empty results. All returned advisories
remain in the report, including records without numerical severity. Unknown advisories block.
The two mapped libxml2 records can receive a reported-path-absent annotation after binary
qualification. They still block: their shared root functions remain, and removing a reported
caller alone does not prove that another route cannot trigger the same defect. No advisory
receives an automatic not-affected disposition or severity suppression.

`eng/test-native-dependencies.ps1` requires the generated HTML/XInclude macros to be absent,
rejects all defined `html[A-Z]*` and `xmlXInclude*` symbols in the static library, and requires
the XML parser symbols to remain. An ASan-linked harness exercises XML parsing, compression
roundtrips, and a read-only-descriptor gzip write failure followed by `gzclearerr`/`gzprintf`.
The harness is ASan-instrumented; the third-party static libraries are not fully
ASan-instrumented. A passing run does not establish sanitizer coverage of every vendor operation.
Source-resource identities, library hashes, catalog digest and successful smoke results are
recorded in `dependency-qualification.json`. Staging preserves that evidence and source IDs.
The advisory review checks the actual linker libraries and the `xml2s.lib` alias against those
hashes. Missing/stale evidence, changed bytes or any new advisory identity block qualification.
Feature absence alone is not clearance; the exact upstream fix commits must also be present in the
pinned source-tag history.

The native backend build also records a `dependency_binding` in its local `build-info.json`:
the exact source-catalog digest, qualification-report digest and names, sizes and SHA-256
digests of every allowed native payload. Staging checks this binding before copying files
and checks the destination bytes again, then preserves `licenses/native/build-binding.json`.
Missing binding metadata requires rebuilding the backend; changing a binary, report, catalog
or inventory fails staging. This prevents accidental mixing of stale build outputs with a
new qualification report. It is a local build-consistency check, not an independently signed
attestation or proof that a compromised build process used the recorded source inputs.

`eng/test-native-build-evidence.ps1` validates matching and reordered inventories and rejects
malformed identities, omitted/duplicate payloads, traversal, replacement binary bytes, stale
qualification reports and changed catalogs using inert temporary fixtures. It executes no
untrusted binary and makes no network requests.

`no-known-advisories` means that a successful OSV commit query returned no records. It is not
a guarantee of safety, complete database coverage or absence of CodeQL findings. This report
is not a complete SPDX or CycloneDX SBOM and does not replace private-runtime/NuGet review.

## Advisory dispositions

The October 23, 2026 commit queries returned these records. The current query still returns both
libxml2 records because OSV lists introduced-only ranges through v2.15.4. The workflow retains
those raw records and verifies the upstream fix commits against the pinned v2.15.4 file history:

| Component | Advisory | Reported affected behavior | Disposition |
|---|---|---|---|
| zlib | [CVE-2026-76844](https://osv.dev/vulnerability/CVE-2026-76844) | Stale gzip write input after a failed write can lead to a heap buffer overflow. | Fixed source built locally; fresh query returned no records and the failed-write regression passed. Full package qualification remains pending. |
| zlib | [CVE-2026-85091](https://osv.dev/vulnerability/CVE-2026-85091) | Non-blocking gzip writes followed by formatted writes can reach an invalid buffer move. | Same fixed source and local regression evidence; comprehensive vendor sanitizer coverage is not established. |
| libxml2 | [OSV-2021-777](https://osv.dev/vulnerability/OSV-2021-777) | Heap use-after-free in an XInclude sibling/copy path. | [Upstream fix 8f5ccada](https://gitlab.gnome.org/GNOME/libxml2/-/commit/8f5ccada05ddd4a1ff8e399ad39fc7cd4bd33325) is present in the pinned tag; XInclude is disabled and its symbols are absent. Raw OSV record remains in the report. |
| libxml2 | [OSV-2024-698](https://osv.dev/vulnerability/OSV-2024-698) | Heap use-after-free in an encoding-close/HTML-save path. | [Upstream fix fcb7a777](https://gitlab.gnome.org/GNOME/libxml2/-/commit/fcb7a777ce19ff2bef020ed2966a0c96e4f4da4c) is present in the pinned tag; HTML is disabled and its symbols are absent. Raw OSV record remains in the report. |

The generated zlib overlay selects the upstream gzip-write fix:
[commit df84af25](https://github.com/madler/zlib/commit/df84af25dc1942490e1d1c899a07619152a46148).
That commit resets pending input after an error. Neither an unchanged upstream version string
nor an assumption that LLVM does not call gzip file APIs substitutes for source/binary evidence.

The upstream fixes address the specific lifetime errors reported by OSS-Fuzz. The first stores
the node returned by `xmlAddNextSibling`/`xmlAddChild` while XInclude copies a node range. The second
defines that output-buffer creation leaves the encoder with its caller on failure, with HTML save
callers closing it explicitly. CI verifies each immutable commit SHA and subject in the pinned
tag's relevant file history, then binds the disabled-feature and absent-symbol proof to the exact
static library bytes. Local validation passes; complete product requalification still depends on
PR CI. This does not claim that all libxml2 defects are fixed or that all XML paths are absent.
No CodeQL finding has been dismissed, and no general security clearance is implied.

The affected helper functions remain present in general libxml2 code, so symbol presence alone
cannot describe these fixes. The verified fixes change the vulnerable caller/ownership contracts:

| Advisory path | Verified upstream change | Build evidence |
|---|---|---|
| `OSV-2021-777` | Copy helpers retain the node pointer returned by sibling/child insertion; the change covers both XInclude range and XPointer copy paths. | Exact commit in `xinclude.c` history; XInclude option macros and symbols absent in the built library. |
| `OSV-2024-698` | Encoder ownership on output-buffer creation failure is explicit; HTML save callers release the encoder on failure. | Exact commit in `xmlIO.c` and `HTMLtree.c` histories; HTML option macros and symbols absent in the built library. |

## CodeQL review remains separate

The existing main revision `901666fdc43aac7c08ff8500a1c8b879df911d37` had 72 open C++
CodeQL records: 17 in downloaded LLVM headers and 55 in vcpkg dependency build sources.
The review snapshot had no first-party source locations. That observation does not clear
the upstream records or prove absence of first-party vulnerabilities.

| Query family | Records | Required review |
|---|---:|---|
| Suspicious pointer scaling | 34 | Allocation/layout invariants and the actual instantiated types. |
| Wrong format argument type | 8 | Vararg types, format conversions and platform ABI. |
| Incorrect logical-not operator use | 7 | Intended Boolean/bitmask expression semantics. |
| Invalid pointer dereference | 6 | Bounds, buffer lifetimes and full caller preconditions. |
| External entity expansion | 4 | Parser options and callers that can supply external resources. |
| Unsafe string concatenation | 4 | Source lengths and destination capacity at every call. |
| Integer multiplication conversion | 3 | Operand limits and arithmetic width before multiplication. |
| Pointer overflow check | 2 | Pointer provenance, integer bounds and supported platform semantics. |
| Non-constant format | 1 | The actual format-string source and caller trust boundary. |
| Path injection | 1 | Catalog input and permitted filesystem access. |
| Filesystem check/use race | 1 | File identity, replacement and read-size handling. |
| Type confusion | 1 | LLVM object/use storage and placement invariants. |

Initial source inspection found explicit capacity checks in the reported zstd RLE write
helper and configurable options in the reported libxml2 parser wrappers. Those observations
are not complete interprocedural proofs. The records remain open; vendor-path location alone
does not justify suppression, exclusion, or automatic risk acceptance.

The protected merge of the resident-span GC change does not publish 0.0.2 or resolve this
dependency review. Native release publication requires remediation or verified binary-specific
applicability evidence, fresh advisory results, and the separate merged-SHA/tag/package gates.
