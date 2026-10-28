<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Native dependency review

Native package execution tests, source analysis, advisory matching and release qualification
provide different evidence. A passing compiler or GC test does not clear an upstream advisory.
This page records the native dependency review for the Sol 0.1.0 native component. The latest
merged-source qualification is tied to SHA `4b70a251ebeca855736e66cfdbe9304be71f15da`; the tag
workflow must repeat the checks for the exact release commit.

## Exact inputs

`eng/native-advisories.json` records the source repositories, release tags, annotated tag objects
and dereferenced commits verified on October 26, 2026. LLVM remains pinned to 23.1.3.
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
Both raw libxml2 records remain in the OSV report. In this exact build, the pinned tag history contains
the verified upstream fixes, and the matching binary qualification proves that HTML/XInclude code and
symbols are absent. Those records are therefore non-blocking under the explicit evidence rule; unknown
records, missing history, mismatched source, or stale binary proof still block. No advisory is deleted
or severity-suppressed.

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
| zlib | [CVE-2026-76844](https://osv.dev/vulnerability/CVE-2026-76844) | Stale gzip write input after a failed write can lead to a heap buffer overflow. | The pinned source includes the upstream fix; the latest commit query returned no advisory records and the hash-bound failed-write regression passed. This is not comprehensive vendor sanitizer coverage. |
| zlib | [CVE-2026-85091](https://osv.dev/vulnerability/CVE-2026-85091) | Non-blocking gzip writes followed by formatted writes can reach an invalid buffer move. | The same fixed source and regression evidence apply; the latest commit query returned no advisory records. This is not comprehensive vendor sanitizer coverage. |
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
static library bytes. The merged-source Native dependency advisories run `40684411758` succeeded on SHA
`4b70a251ebeca855736e66cfdbe9304be71f15da`. Its OSV results were LLVM 23.1.3: 0 records;
zlib 1.3.2: 0; zstd 1.5.7: 0; libxml2 2.15.4: 2 raw records, both with verified upstream
fixes and 0 blocking. The `v0.1.0` tag run must produce its own successful result. This does not
claim that all libxml2 defects are fixed or that all XML paths are absent.
No CodeQL finding has been dismissed, and no general security clearance is implied.

The affected helper functions remain present in general libxml2 code, so symbol presence alone
cannot describe these fixes. The verified fixes change the vulnerable caller/ownership contracts:

| Advisory path | Verified upstream change | Build evidence |
|---|---|---|
| `OSV-2021-777` | Copy helpers retain the node pointer returned by sibling/child insertion; the change covers both XInclude range and XPointer copy paths. | Exact commit in `xinclude.c` history; XInclude option macros and symbols absent in the built library. |
| `OSV-2024-698` | Encoder ownership on output-buffer creation failure is explicit; HTML save callers release the encoder on failure. | Exact commit in `xmlIO.c` and `HTMLtree.c` histories; HTML option macros and symbols absent in the built library. |

## CodeQL review and dependency source boundary

At main commit `4b70a251ebeca855736e66cfdbe9304be71f15da`, GitHub reported 73 open C++ alerts.
Their CodeQL locations are all under ignored `out/` build output: pinned LLVM 23.1.3 headers or
vcpkg build sources for zlib 1.3.2, zstd 1.5.7 and libxml2 2.15.4. The alert API showed no
tracked first-party C++ locations at that revision. These alerts are static-analysis findings in
upstream code; their presence alone does not establish exploitability or a Sol-owned defect.

| Query family | Alerts |
|---|---:|
| Suspicious pointer scaling | 34 |
| Wrong format argument type | 8 |
| Incorrect logical-not operator use | 7 |
| Invalid pointer dereference | 6 |
| External entity expansion | 4 |
| Unsafe string concatenation | 4 |
| Integer multiplication conversion | 3 |
| Pointer overflow check | 2 |
| Non-constant format | 2 |
| Path injection | 1 |
| Filesystem check/use race | 1 |
| Type confusion | 1 |
| **Total** | **73** |

The C++ CodeQL job now places the LLVM toolchain, vcpkg source/build trees and installed native
headers under the runner's temporary directory, outside the checkout. It still builds and analyzes
Sol's tracked C++/CLI and native sources. The workflow does not use `paths-ignore`, CodeQL query
filters or alert dismissals. Native source versions and known advisories continue through the
hash-pinned OSV commit review and binary qualification described above.

This source-boundary correction does not patch the upstream files or clear their findings in those
upstream repositories. If a future scan reports a finding in a tracked Sol source file, it remains
in scope and must be fixed or explicitly reviewed on its own evidence. GitHub's treatment of prior
`out/` locations after reanalysis is not evidence that an upstream defect was fixed.

The protected merge of the resident-span GC change does not publish 0.1.0. A release still requires
successful CI for the exact merge and tag commits, successful native advisory qualification, and
a verified package built from the tag.