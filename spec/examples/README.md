# Source examples

These examples illustrate complete source shapes and intended behavior. They are syntax/AST
fixtures, not runnable programs in the current release. Every file carries its own implementation
status. Native execution, standard library implementations, and full semantic verification remain
planned even where the syntax is already accepted.

| Example | Explicit source set | Intended behavior |
|---|---|---|
| [Console greeting](console/Greeting.sol) | One file, plus the future standard library | Main prints a greeting and each argument |
| [Argument ordering](calls/ArgumentOrdering.sol) | One file | Ordered named/positional calls, middle vararg, defaults and trailing lambda |
| [Default inheritance](calls/InheritedDefaults.sol) | One file | Base default contract with derived dispatch |
| [Checked configuration](errors/Configuration.sol) | One file, plus the future standard library | Local recovery and declared checked failures |
| [Nullable arrays](data/NullableArrays.sol) | One file | Distinguishes element and container nullability |
| [Value copying](data/Points.sol) | One file | Struct copy changes independently |
| [Collections](collections/VectorUsage.sol) | One file, plus the future standard library | Vector snapshot and mutable borrow boundaries |
| [Payload switch](flow/Commands.sol) | One file | Enum payload dispatch and exhaustive returns |
| [Delegated settings](delegation/Settings.sol) | One file, plus the future standard library | Separate owner state and shared property descriptor |
| [Generic bounds](generics/NamedItems.sol) | One file | Nominal constraints and explicit generic calls |
| [Native clock](native/Clock.sol) | One file | Verified unsafe Windows C call in a later backend |
| [Partial type](modules/Panel.Storage.sol) | Both `Panel.Storage.sol` and [Panel.Behavior.sol](modules/Panel.Behavior.sol) | One type split across files of the same logical module |

Source sets are explicit. A directory path does not determine a module, create a project root or
discover dependencies. Several examples intentionally define their own `Main`; do not combine
independent application examples into a single executable compilation.
