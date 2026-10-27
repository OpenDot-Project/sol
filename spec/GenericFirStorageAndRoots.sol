// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative storage and tracing contract; generic layout verification remains incomplete.
module source.spec.generic_fir_storage_and_roots

// Generic concrete storage, managed arrays, scoped views, and Yellow Tea roots
// ===========================================================================
//
// This contract defines the storage and tracing obligations that follow when a generic parameter
// is replaced with a concrete Sol type. It is separate from generic inference, source overload
// selection, and the current FIR specialization implementation. A closed type tuple does not by
// itself prove a concrete memory layout, a safe GC scan shape, or a valid lifetime.
//
// The words MUST, MUST NOT, SHOULD, SHOULD NOT, and MAY have their ordinary standards meaning.
// Every native admission decision MUST be supported by the compiler's source identity, concrete
// type representation, ownership/lifetime analysis, verifier, and target-specific runtime contract.
// A missing proof is a rejection or a deferred source result; it is never permission to guess.

// Current implementation status
// ------------------------------
//
// Development builds have a restricted generic FIR slice for direct calls to module-level generic
// free functions. It substitutes already-bound body facts and admits only type arguments supported
// by the current verified native type set: selected primitives, String, empty tuple where the
// function result requires it, and nested arrays with supported leaves: selected primitives,
// String, or non-null receiver-free function pointers. The function-pointer element descriptor is
// scalar: it has pointer width but no managed-reference scan shape. This does not admit a closure
// environment or captured value. It is not a general generic type-layout engine.
//
// The existing native runtime has provisional object, String, and array headers, precise descriptor
// scan shapes for the generated bootstrap subset, registered roots, polling, and insertion-barrier
// stores. These mechanisms are experimental and do not establish ABI 1 or a latency guarantee.
// Their current inventory is recorded in `docs/yellow-tea/current-implementation.md`.
//
// Generic nominal owners, structs containing T, arbitrary nullable representations, inline fixed
// arrays, `std.collections` implementations, `Span<T>`, `ReadOnlySpan<T>`, general ref-like lowering,
// per-instance borrow verification, and generic collection descriptors are not admitted by the
// current native specialization slice. The collection signatures in `std/StandardLibrary.sol` are
// intended surface contracts, not shipped implementations.
//
// No clause here claims that each normative scenario has passed a compiler, runtime, sanitizer, or
// collector test. The paired source examples record expected status separately from implementation
// status.

// 1. Identities and representation categories
// --------------------------------------------
//
// A source type is identified by its resolved semantic owner and structure, not its printed name.
// A generic parameter is identified by its declaration owner and ordinal. Two parameters both
// spelled T are distinct when their owners differ. A concrete substitution MUST target the exact
// owner/ordinal pair selected during source analysis.
//
// A concrete type descriptor is target-specific, immutable metadata describing the source type's
// layout and scan behavior. It is not a generic parameter token and not a display string. A
// descriptor version MUST encode every layout fact that native consumers need to agree on, at least:
//
// - source declaration identity and constructed generic arguments when applicable;
// - layout kind and version, size or size rule, and required alignment;
// - direct and recursively embedded managed-reference scan locations;
// - nullable-presence and tagged-payload locations where those representations are used;
// - array stride, element descriptor, and element scan program for variable-length storage;
// - base/interface/dispatch metadata only when an admitted object representation needs it;
// - field mutability/write-barrier classification and runtime ownership domain;
// - target triple, pointer width, runtime ABI identity, and compiler/runtime compatibility.
//
// A compiler MUST NOT infer that a type is pointer-free because its current sample value has no
// reference, or because its field names look numeric. It MUST derive the scan shape from the
// selected concrete type, including nested value fields and active variants.
//
// Source values have distinct storage categories:
//
// | Source category | Storage model | Collector interpretation |
// |---|---|---|
// | Primitive | Target-defined scalar value | Never scan its bits as a reference. |
// | Class/object/String | Managed reference to a heap object | Trace the reference, then its descriptor. |
// | `[]T` | Managed array reference and descriptor-governed element region | Trace the owner; scan elements using the concrete element descriptor. |
// | Struct/tuple | Inline value unless an explicit boxing rule applies | Recursively scan only declared reference fields. |
// | Nullable reference | Null or the original reference category | Trace only when present. |
// | Nullable value | Presence state plus concrete payload representation | Inspect the tag before scanning the payload. |
// | Enum with payload | Tag plus active alternative's payload | Scan only the selected alternative. |
// | Ref struct / span | Scoped value with borrowed storage provenance | Do not heap-trace the view as an owner; retain its base owner or stack lifetime proof. |
// | Receiver-free function value | Raw code pointer | Never scan its bits as a managed reference. |
// | Closure value | Code pointer plus environment ownership | Trace the environment and its managed captures, not the code address. |
// | `ref`/`out` location | Base owner plus location identity/offset | Keep the base alive; do not register a naked interior address as an object. |
//
// These categories may share an implementation ABI only if the descriptor and verifier preserve
// their observable source semantics. For example, a nullable reference and a nullable struct cannot
// be merged into one representation merely because both can be absent.

// 2. Substitution is followed by representation analysis
// -------------------------------------------------------
//
// Generic substitution MUST preserve every constructor and annotation in a type. Substitution is
// simultaneous and owner-sensitive. It does not flatten arrays, drop nullability, erase passing
// modes, merge distinct owners, or infer a layout from the spelling of the replacement type.
//
// The following transformations are distinct:
//
// - `T` substituted with `String` produces a managed reference value.
// - `T` substituted with `int` produces a primitive value.
// - `T` substituted with `[]String` produces a managed array reference whose elements are String
//   references and whose descriptor scans those element slots.
// - `T` substituted with `[]String?` produces an array whose elements may be null. Each non-null
//   element remains a managed reference; the array reference itself is independently non-null.
// - `T` substituted with `([]String)?` produces an optional array reference. When present, the
//   array's elements retain their original non-null String type.
// - `T` substituted with `[]([]int)` is a nested array value. Its outer descriptor would scan
//   managed references to inner arrays; this layout is outside the current native generic ABI.
// - `T` substituted with `Span<int>` is ref-like and requires a scoped borrow proof; it is not an
//   ordinary object or a stable native pointer.
//
// The spellings `[]String?` and `[](String?)` denote an array whose element type is nullable
// String. The spelling `([]String)?` denotes a nullable array container with non-null String
// elements. A parser, source type system, FIR verifier, runtime, and diagnostic formatter MUST
// agree on these shapes. Parentheses around the element type MUST NOT move nullability to the array.
//
// Inference and overload selection happen before native layout admission. Backend capability MUST
// NOT select a different source overload. The chosen tuple is checked against source constraints,
// then concrete representation rules. A type's constraint satisfaction does not establish that its
// fields are storable, its values are traceable, or its calling convention is supported.

// 3. Primitive, reference, and aggregate layouts
// -----------------------------------------------
//
// Primitive fields occupy target-defined scalar storage. Their bits are not roots, even when the
// numeric value equals a heap address. The descriptor for a primitive-only value has no managed
// edges. Extended floating-point types and every integer width still need an explicit target ABI;
// source availability does not imply native support in every artifact.
//
// A class or object value is a reference to a managed allocation. Its allocation header identifies
// a descriptor. The descriptor determines the object's fields and inherited layout. A generic class
// application such as `Box<String>` needs a concrete descriptor whose field `value: T` is traced as
// a String reference. `Box<int>` may have a scalar-only field. If both instantiations share one
// erased layout, that layout MUST still carry enough runtime metadata to scan each value correctly.
//
// A struct is copied by value and has no class inheritance. Its layout may inline primitive,
// reference, nullable, tuple, enum, or nested-struct fields. The descriptor recursively composes
// scan operations for those fields. Copy and assignment preserve value semantics; copying a
// reference field copies the reference, not the referenced object.
//
// Interface storage for a value type requires an explicit representation/boxing contract. It MUST
// not silently reinterpret inline struct bytes as a managed object pointer. Boxing, unboxing,
// runtime type tests, virtual dispatch, and constrained generic calls all need verifier and runtime
// support before they are admitted in native FIR.
//
// A partial generic type has one semantic declaration identity after all compatible parts are
// merged. Every part must agree on type parameter owner/ordinal identity and constraints. Layout
// order is determined by one documented source rule, not filesystem enumeration or partial-file
// arrival order. Incompatible partial declarations fail before descriptor generation.
//
// The descriptor builder MUST reject recursive by-value layouts that have no finite size. Cycles
// through managed references are allowed when the referenced descriptor is available. Base-class
// layout, field offsets, alignment, zero-valid defaults, and enum payload offsets are resolved from
// the same canonical declaration graph used by type checking.

// 4. Dynamic arrays and nullable element identity
// -----------------------------------------------
//
// A dynamic array is a fixed-length managed object. Its object header contains the length, element
// stride, and concrete element descriptor or a verified equivalent. Allocation arithmetic checks
// length times stride plus header for overflow before reserving memory. A zero-length array remains
// a valid array object when created by the source constructor; it is not a null reference.
//
// The array owner reference and an element reference are separate edges. A frame keeping an array
// alive does not make all interior addresses valid independently. Indexing validates bounds,
// computes a checked offset, and performs a barrier-aware store for a managed reference element.
//
// | Type | Container can be null? | Element can be null? | Required scan shape |
// |---|---|---|---|
// | `[]int` | No | No | Root owner; do not scan scalar payloads. |
// | `[]String` | No | No | Root owner and scan every String slot. |
// | `[]String?` | No | Yes | Root owner and scan each non-null element slot. |
// | `[](String?)` | No | Yes | Same semantic shape as `[]String?`. |
// | `([]String)?` | Yes | No | Root nullable owner when present; scan String element slots. |
// | `([]String?)?` | Yes | Yes | Optional owner plus nullable-reference element slots. |
// | `[4]String` | Inline fixed array | No | Recursively scan its inline reference fields. |
// | `[][]String` | No | Inner array values are references | Root outer owner and trace inner-array element slots. |
//
// The current native runtime supports nested managed arrays with qualified primitive, String, and
// non-null function-pointer leaves. Reference arrays trace their managed array elements; function
// pointers use scalar pointer-width storage with no scan shape. Nullable element arrays, inline
// fixed arrays, closure environments, and arbitrary element descriptors remain outside the current
// generic specialization boundary.
//
// `for-in` over a supported array evaluates its source once, saves the fixed length, roots the array,
// loads elements in index order, and copies each item into a source local. The loop item is not an
// implicit writable array slot. A write through explicit indexing uses the array's concrete element
// descriptor and required reference barrier. Body execution may allocate or collect; the array and
// any loaded reference value live across that collection must be published in the frame roots.

// 5. Generic collections and scoped spans
// ----------------------------------------
//
// The intended `std.collections` surface includes `Vector<E>`, `HashSet<E: std.hash.Hashable>`,
// `HashMap<K: std.hash.Hashable, V>`, `TreeSet<E: std.order.Ordering>`,
// `TreeMap<K: std.order.Ordering, V>`, `Queue<T>`, `Deque<T>`, `Stack<T>`,
// `PriorityQueue<T>`, `Span<T>`, and `ReadOnlySpan<T>`. These are source contracts, not current
// implementations. The required public APIs and user-visible errors are specified separately.
//
// Heap collections own managed backing storage. Their descriptor must account for all live
// element/key/value slots, occupancy markers, capacity, resize transitions, and tombstones where
// applicable. A hash table containing reference keys cannot expose raw stale keys after deletion;
// every mutation that publishes a reference uses the correct barrier. A resize keeps the source
// storage alive until all live elements have been transferred or the operation fails without
// publishing a partial table.
//
// Collection-specific invariants include:
//
// - `Vector<T>` has contiguous logical element order and a separately specified capacity policy.
//   Resizing allocates new backing storage and transfers the live element values safely.
// - `HashSet<T>` and `HashMap<K,V>` use the declared Hashable constraints and do not promise that
//   iteration order is serialization order. Keys remain strongly reachable while stored.
// - `TreeSet<T>` and `TreeMap<K,V>` use the declared Ordering constraints. Comparisons do not
//   replace equality/hash semantics in hash containers.
// - `Queue<T>` and `Deque<T>` clear removed slots so dead references do not remain artificially
//   reachable. Their wraparound arithmetic is checked against capacity.
// - `Stack<T>` traces only active values, not unused capacity bytes.
// - `PriorityQueue<T>` traces queued payloads and any reference-containing ordering metadata while
//   preserving stable failure behavior if comparison throws.
//
// `Span<T>` and `ReadOnlySpan<T>` are scoped ref structs representing a view over an owner and a
// bounded window. A view is not the collection and does not extend its lifetime. If it refers to a
// managed array, its root obligation is to keep that array owner alive while the view is live. If it
// refers to stack storage, the compiler proves that the origin scope outlives each access. The
// collector never treats the view's interior address as an allocation root.
//
// `ReadOnlySpan<T>` prevents writes through that view; it does not make the backing owner immutable
// through other aliases. A copied span header copies the view (owner/provenance, offset, length,
// access mode); it does not copy elements. Slicing composes checked offsets and lengths while
// retaining the same origin. A writable `Span<T>` may update elements only through explicit,
// bounds-checked operations with the appropriate reference barrier.
//
// Ref-like values MUST NOT be boxed, stored in ordinary managed heap objects, placed in ordinary
// managed arrays, captured by escaping closures, sent to another thread, retained by native code,
// or used after their origin lifetime ends. A generic constraint such as `allows ref struct` only
// admits a candidate type; it does not prove the generic body obeys those rules.

// 6. ref/out, interior locations, and copies
// ------------------------------------------
//
// A `ref` or `out` parameter identifies writable storage and preserves the exact source type. Its
// ABI may be an address, but that address is not a managed object identity. The compiler tracks its
// base storage, offset/path, mutability, and lifetime. If its base is a managed object or array, the
// base object remains rooted for the complete use interval. If it refers to a stack local, the
// owning frame cannot be popped before the callee and all cleanup complete.
//
// Ref/out substitution requires exact storage type evidence. A `ref short` location cannot be
// passed to `ref T` with `T = int` merely because `short` converts to `int`. A caller cannot pass a
// nullable storage slot to a non-null generic reference parameter by pretending that a flow fact
// changed the slot's declared layout. An `out` location's concrete type comes from the selected
// declaration and is assigned on every normal return edge.
//
// When a generic call takes a ref/out value whose concrete type contains managed references, the
// call boundary must preserve GC visibility in both caller and callee frames. A callee write to a
// heap field through an interior reference still goes through the same barrier protocol as a
// direct field assignment. Returning a ref preserves the source owner lifetime and cannot extend a
// stack/local lifetime.
//
// Copying a struct containing references creates another set of reference values. Both copies are
// traced independently while live. Mutating one copy changes only its inline fields, but a copied
// reference field still denotes the shared object. A byref-like struct's copy does not change the
// origin lifetime and cannot make a borrow escape-safe.

// 7. Descriptors and exact scan programs
// --------------------------------------
//
// A descriptor scan program is derived from the concrete layout. It may contain scalar skips,
// reference slots, nested descriptor calls, nullable tags, tagged alternative dispatch, and
// variable-length array iteration. Every offset is validated against the concrete object size and
// alignment. The runtime checks descriptor version and object allocation bounds before reading a
// scan word.
//
// For a fixed-layout class or struct, the scan program enumerates every reference-bearing field.
// Inherited fields and partial-type fields are included in one deterministic layout. A nullable
// reference is scanned as an ordinary reference slot. A nullable value is scanned only if its
// presence state says a value exists. An enum scans the active payload only; inactive bytes are not
// interpreted as references.
//
// For a dynamic array, the descriptor contains an element scan program and the array header supplies
// the exact count and stride. The runtime validates `count * stride` before traversing. Reference
// slots must lie inside each element stride, and element scan state cannot cross the next element.
// Empty arrays still retain a valid descriptor and zero count. Scalar arrays have no per-element
// reference scan, but the array object itself remains a managed allocation.
//
// A pointer-shaped integer or an untrusted external descriptor cannot introduce a scan edge. Type
// identity, field identity, nullable state, and layout compatibility are validated before marking.
// A bad descriptor results in a controlled runtime failure/abort before an invalid memory read; it
// is never repaired by scanning the entire object conservatively.

// 8. Root frames, safepoints, and register liveness
// ------------------------------------------------
//
// A generated frame descriptor describes the root slots and exact scan shape for one concrete
// function instance. A generic function specialized for two concrete types may require two
// different frame descriptors even when its source body is identical. Descriptor identity is tied
// to the exact specialization tuple, target, and runtime layout contract.
//
// Before a managed thread acknowledges a collection handshake, the compiler spills every live
// managed reference needed at that safepoint into registered root storage. The runtime scans only
// those slots and explicitly registered strong handles. A stale register value is not a root by
// accident; a live reference cannot be omitted because it was introduced by substitution or
// produced by a generic call.
//
// Required roots include:
//
// - reference parameters and local slots that are live across a safepoint;
// - reference temporaries used by an operand after another operand can allocate;
// - generic call results before the caller stores or returns them;
// - array owners, loaded reference items, and array receivers used after a poll;
// - references nested in concrete struct, tuple, enum, or nullable-value payloads;
// - capture cells and closure environments when closure support is admitted;
// - exception values, suppressed causes, cleanup state, and native handles during unwinding;
// - owner roots for interior `ref`/`out` locations and stack-scoped views over managed arrays.
//
// A root map must be precise at each collection point. Conservative root retention may extend
// object lifetime, but it cannot excuse an omitted live reference. It also cannot turn an integer
// or untracked interior pointer into a managed object identity.
//
// The current native bootstrap conservatively retains some generated reference locals and
// temporaries until reuse or function exit. Fine-grained liveness and general nested value scan
// programs remain incomplete. Generic FIR specialization does not add its own independent root
// planner; it must use the backend/runtime mechanisms and reject layouts they cannot describe.

// 9. Store barriers and mutation publication
// -------------------------------------------
//
// A managed reference store may create an edge from an already-black owner to a white object. The
// collector's concurrent marking invariant requires the exact runtime barrier policy to preserve
// that edge. Compiler-generated stores to object fields, array elements, struct fields nested in
// managed objects, reference-containing collection entries, root slots, and handle records must
// use their defined publication protocol.
//
// Barrier placement follows the concrete field's scan classification. A scalar-only field does not
// need a reference insertion barrier. A generic `T` field cannot be classified while open; after
// substitution, its descriptor and store lowering agree on whether the slot is a managed edge.
// This choice cannot be deferred to a raw memcpy if the copied value contains references.
//
// During construction, unpublished object state may use a documented initialization path only
// while the object is not reachable by the collector. Publication of the initialized object must
// establish its complete scan shape. A constructor that leaks `this`, stores into a static root, or
// invokes a callback before initialization completes needs the ordinary publication/barrier rules.
//
// Deleting or replacing a reference slot must not make a still-live copied reference disappear
// before it has another protected root/edge. Collection of dead targets is allowed only after all
// reachable references and temporary roots have been accounted for. Collection, barriers,
// allocation epochs, and root handshakes must share one documented memory-ordering protocol.

// 10. Closures, cleanup, and exceptional edges
// --------------------------------------------
//
// A captured mutable local is represented by a shared capture cell when source semantics require
// aliasing across closures. The cell is a managed owner with a descriptor for its captured fields.
// Captured immutable values use the specified shallow-copy semantics. A closure's code pointer is
// not scanned; its environment reference is.
//
// A ref struct or span cannot be copied into an escaping heap closure. An immediate non-escaping
// callback is admissible only if the language and compiler prove that its complete call and cleanup
// finish before the borrowed lifetime ends. Storing the callback in a collection, field, task,
// process message, or native callback registry makes it escaping.
//
// Exception carriers and pending Throwables own strong runtime handles across native frames. Catch
// locals, `finally`, `defer`, and suppressed-exception chains may extend liveness beyond the last
// source-level read. Root descriptors and frame pop order cover all such cleanup paths. A generic
// type argument that changes an effect or payload's scan shape is verified per concrete instance.
//
// The native generic FIR slice currently rejects source catch/finally/defer lowering and general
// closures. Runtime C++ carrier tests do not prove that generated generic Sol functions preserve
// roots across Windows exception unwinding.

// 11. Host/guest process ownership
// -------------------------------
//
// Solc and the LLVM bridge execute in the .NET host process. The host CLR heap and its GC own
// compiler objects only. An AOT Sol application owns its own native Yellow Tea heap. ORC execution
// runs in a separate native guest process with its own runtime, descriptors, root frames, handles,
// and managed objects.
//
// Only documented transport values such as source/bitcode bytes, arguments, streams, diagnostics,
// cancellation records, and process status cross the host/guest boundary. CLR object references,
// Yellow Tea pointers, descriptor addresses, root frames, callback pointers, and handles do not.
// A raw address cannot be smuggled through a serialized integer and later treated as an object.
//
// Process isolation protects runtime ownership but is not an OS sandbox. Guest imports, pipe
// framing, process lifetime, output bounds, cancellation, and exit status have separate validation
// requirements. Static SolRT linkage does not merge the guest collector into Solc or create shared
// roots. Multiple libraries inside one AOT process may share a collector only under an explicitly
// qualified runtime ownership/ABI rule.

// 12. Verification and diagnostic ownership
// ------------------------------------------
//
// Before native code generation, the compiler verifies the complete source-selected instance graph:
//
// 1. Every type identity belongs to the active compilation workspace.
// 2. Every generic instance has exactly the declaration's ordered type-argument count.
// 3. Each argument is closed, satisfies the declared constraints, and has an admitted concrete ABI.
// 4. Every substituted signature, local, expression, result, and effect agrees with source binding.
// 5. Every generic call targets the exact function instance selected by source overload resolution.
// 6. Every reachable instance has one owner and every emitted instance is reachable from a root.
// 7. Every concrete field/array/tuple/enum layout has a descriptor compatible with the selected
//    target and runtime ABI.
// 8. Every root and barrier obligation is satisfied for all safepoints, normal exits, and exceptional
//    cleanup edges supported by the native subset.
// 9. Every borrowed/ref-like location has a proven owner and scope; no escaping use survives.
// 10. All artifact output is withheld if any required layout, root, barrier, or verifier proof fails.
//
// The current independent FIR verifier checks supported source type identities and call/signature
// facts but does not independently validate the complete descriptor/layout/root/borrow contract in
// this specification. A future implementation must add those proof objects or restrict source
// admission to the qualified runtime subset. The compiler must not report complete generic storage
// support based only on successful FIR JSON serialization.
//
// `SOL2400` owns a source shape rejected by the native admission boundary. `SOL2402` owns a
// constructed FIR graph that violates its source/verifier contract. `SOL2403` owns a specialization
// resource limit. A GC descriptor/runtime invariant failure is a controlled runtime failure under
// the native runtime diagnostics contract; it is not silently rewritten as a source type error.

// 13. Compatibility and artifact contracts
// -----------------------------------------
//
// A descriptor layout version changes when offsets, alignment, nullable tags, element strides,
// scan programs, handles, frame maps, or runtime ownership change incompatibly. A library/container
// must pin the runtime ABI and compiler identity required to interpret those descriptors.
//
// A generic function is not automatically a stable C symbol or a `.solib` export. A concrete C
// wrapper may call a private closed instance only if the wrapper ABI and its transitive root,
// runtime, and object dependencies are fully declared. A consumer cannot request an instance from
// an external open generic declaration until a versioned identity/constraint/ABI contract exists.
//
// AOT and ORC consume the same verified source semantics. They may differ in execution and output
// mechanics, but they MUST agree on concrete type layout, object tracing, root lifetime, barrier
// placement, exception ownership, and artifact admission. SolRT dynamic versus static linkage is a
// deployment choice and does not change type identity or layout.

// 14. Required conformance scenarios
// ----------------------------------
//
// The accompanying `examples/semantics/GenericFirStorageAndRoots.sol` records the detailed case
// matrix. At minimum, conformance must establish:
//
// - primitive, String, and supported nested-array specializations preserve the current admitted
//   FIR/native subset without claiming generic aggregates are implemented;
// - nullable array element and nullable container forms remain distinct under substitution;
// - arrays of references trace only actual reference slots, while scalar values resembling addresses
//   are never treated as roots;
// - nested arrays with unsupported leaf layouts, generic value layouts, generic collection
//   instantiations, and ref-like values are rejected before native emission until their descriptors
//   and lifetime proofs exist;
// - reference stores through fields, arrays, structs, collection slots, roots, and ref/out aliases
//   use the concrete barrier classification;
// - collection resize/remove/failure paths do not publish stale, duplicate, or unrooted references;
// - spans preserve their originating managed owner or stack lifetime, bounds, and access mode;
// - stack maps keep every substituted reference alive across calls, allocations, loop polls, and
//   exceptional cleanup;
// - host CLR references never cross into the ORC guest and guest object handles never cross out;
// - descriptor corruption, foreign IDs, incomplete payload tags, overflowed sizes, and invalid
//   interior offsets fail before unsafe scanning or memory access;
// - AOT, ORC, C library, and container outputs are each checked against their declared root and
//   runtime ownership contract, not inferred from one successful executable.

// 15. Explicit non-promises
// -------------------------
//
// This contract does not promise moving collection, pinning semantics, atomic snapshots of
// multi-field structs, deterministic collection timing, lock-free allocation, bounded wall-clock
// pauses, automatic thread safety for collections, or a public reflection ABI for descriptors.
// It does not grant native lifetime to a Span, make a nullable value pointer-sized, make a generic
// parameter reified, or turn CLR GC into the Sol object collector.
//
// The current collector is experimental. Root inventory and active descriptor shape can dominate
// pause and throughput costs under load. Any claim about locality, barriers, memory pressure, pause
// time, or production readiness requires target workload measurement and remains outside this
// source contract.
