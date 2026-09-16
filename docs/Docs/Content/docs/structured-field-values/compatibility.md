---
title: Structured Field Values ownership and compatibility
sidebarLabel: Ownership and compatibility
description: Mutable-node ownership, value equality, and migration from earlier APIs.
order: 4
section: Structured Field Values
---

# Structured Field Values ownership and compatibility

## Ownership and Equality

Bare values have value equality and no mutable state. Byte sequences copy
incoming arrays, and copy-out operations cannot change the stored bytes or hash.
Boolean singletons are safe to reuse as bare values.

Items, inner lists, parameters, and collections are mutable and use reference
equality. Item/inner-list constructors copy supplied parameter collections.
Mutate parameters through their owning node; a `StructuredFieldMember` does not
introduce a second collection. Explicitly sharing a mutable item between lists
is possible, so modifying that item is visible to both lists.

## Breaking Changes

- Scalar classes now derive from `BareItem`, not `StructuredFieldItem`. Wrap
  values in `new StructuredFieldItem(bareValue)` before attaching parameters;
  inspect parsed scalars through `item.Value`.
- Replace `ListMember` and `DictionaryMember` with `StructuredFieldMember`.
  Supply parameters to the item or inner-list constructor, not `FromItem`.
- Replace null-valued parameters with `BooleanItem.True`. `TryGetValue` returning
  true always yields a non-null bare value.
- Replace mutable byte-array access with `.Bytes` for reading or `.ToArray()`
  for a copy.
- Replace `TokenMember`, `TokenValue`, `TokenParameter`, `TokenElements`, and
  `TokenInnerList` with the corresponding ordinary method and
  `type: ItemType.Token`.
- Mapper models and nested models must be classes; propagate `class, new()`
  constraints through generic helpers. Invalid mappings fail at construction.
- Empty list/dictionary input can now succeed in `TryParse`. HTTP helpers
  distinguish a missing header from a present empty value.
- Explicit Boolean true parameters serialize in shorthand. `ToString()` remains
  diagnostic-only and must not be used as a substitute for serialization.
