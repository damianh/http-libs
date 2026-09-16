---
title: Structured Field Values API reference
sidebarLabel: API reference
description: Parser, serializer, object model, mapper, and builder contracts.
order: 2
section: Structured Field Values
---

# Structured Field Values API reference

## StructuredFieldParser

Static class for parsing RFC 9651 structured field values.

| Method | Returns | Description |
|--------|---------|-------------|
| `ParseItem(string input)` | `StructuredFieldItem` | Parses an item with optional parameters |
| `ParseBareItem(string input)` | `BareItem` | Parses a scalar, rejecting parameters |
| `ParseList(string input)` | `StructuredFieldList` | Parses a list of items and/or inner lists (RFC 8941 §4.2.1) |
| `ParseDictionary(string input)` | `StructuredFieldDictionary` | Parses a dictionary of key→member pairs (RFC 8941 §4.2.2) |

All methods throw `ArgumentNullException` for null input and `StructuredFieldParseException` for malformed input.
Empty strings are valid lists/dictionaries but not items. Duplicate dictionary
and parameter keys keep their original position and use the last parsed value.
Implicit Boolean true parameters (`;flag`) and explicit ones (`;flag=?1`) produce
the same bare Boolean value.

## StructuredFieldSerializer

Static class for serializing structured field values to their canonical wire form.

| Method | Returns | Description |
|--------|---------|-------------|
| `SerializeItem(StructuredFieldItem item)` | `string` | Serializes an item with parameters (RFC 8941 §4.1.3) |
| `SerializeList(StructuredFieldList list)` | `string` | Serializes a list (RFC 8941 §4.1.1) |
| `SerializeDictionary(StructuredFieldDictionary dictionary)` | `string` | Serializes a dictionary (RFC 8941 §4.1.2) |
| `SerializeBareItem(BareItem value)` | `string` | Serializes a scalar without parameters |
| `SerializeInnerList(InnerList list)` | `string` | Serializes a parenthesized inner list with its parameters |
| `SerializeMember(StructuredFieldMember member)` | `string` | Serializes an item or inner list without a dictionary key |

All methods use the same wire writer. Parameters and dictionary entries retain
insertion order, and Boolean true uses shorthand where permitted.

**`ToString()` is diagnostic-only, not wire output.** Always use
`StructuredFieldSerializer` or the mapper's `Serialize` method when writing
headers. Diagnostic strings need not be quoted, complete, canonical, or
round-trippable.

## StructuredFieldMapper\<T\>

A cached, reusable mapper that converts a structured field value to and from a
POCO of type `T`, constrained to `class, new()`.

**Factory methods** (choose based on the RFC 8941 field type):

| Factory | When to use |
|---------|------------|
| `StructuredFieldMapper<T>.Dictionary(Action<DictionaryBuilder<T>> configure)` | Field is a Dictionary (e.g. `Priority`) |
| `StructuredFieldMapper<T>.List(Action<ListBuilder<T>> configure)` | Field is an RFC 8941 List |
| `StructuredFieldMapper<T>.Item(Action<ItemBuilder<T>> configure)` | Field is an RFC 8941 Item |

**Instance methods:**

| Method | Description |
|--------|-------------|
| `T Parse(string input)` | Parses the header value. Throws `StructuredFieldParseException` on failure. |
| `bool TryParse(string? input, out T? result)` | Returns false instead of throwing for missing or malformed input. |
| `string Serialize(T value)` | Serializes the POCO to its canonical RFC 8941 string. |

`TryParse` agrees with `Parse` for valid input: an empty list or optional-only
dictionary succeeds; an empty dictionary with required mappings fails. `null`
means missing input and returns false. Malformed values and mapping mismatches
return false, but configuration and user property-accessor exceptions are not
silently swallowed.

The mapper is a **projection**, not a lossless document editor: unknown
members/parameters are ignored and are not retained on serialization. Use the
object model when preserving all fields and their order matters.

See [type mapping](/docs/structured-field-values/type-mapping/) for CLR type
inference and independent presence rules.

## Item Types

All scalar types extend immutable `BareItem`; they do not carry parameters.
`StructuredFieldItem` wraps a bare value in its `.Value` property and owns one
mutable `.Parameters` collection.

| Type | CLR value | Wire format | Example |
|------|-----------|---------------------|---------|
| `IntegerItem` | `long` via `.LongValue` | Signed integer | `42`, `-1` |
| `DecimalItem` | `decimal` via `.DecimalValue` | Up to 12 integer digits and 3 fractional digits | `3.14` |
| `StringItem` | `string` via `.StringValue` | Quoted string | `"hello"` |
| `TokenItem` | `string` via `.TokenValue` | Unquoted token | `gzip`, `*` |
| `ByteSequenceItem` | Read-only `.Bytes`; copy out with `.ToArray()` | `:base64:` | `:aGVsbG8=:` |
| `BooleanItem` | `bool` via `.BooleanValue` | `?0` / `?1` | `?1` |
| `DateItem` | Unix seconds as `long` via `.UnixSeconds` | `@` followed by an integer | `@1659578233` |
| `DisplayStringItem` | Unicode `string` via `.StringValue` | UTF-8 percent encoding inside `%"..."` | `%"caf%c3%a9"` |

Decimals range from `-999999999999.999` through `999999999999.999`.
Construction rejects excess fractional precision rather than silently rounding.
Dates support the full signed 15-digit integer range, not just the narrower
`DateTimeOffset` range. Display Strings reject malformed Unicode rather than
silently replacing it; ordinary Strings remain printable ASCII-only.

## Collection Types

| Type | Description |
|------|-------------|
| `StructuredFieldList` | Ordered list of `StructuredFieldMember` (item or inner list). Supports `Add`, `AddRange`, count, and indexer access. |
| `StructuredFieldDictionary` | Ordered dictionary of `string` to `StructuredFieldMember`. Supports enumeration and indexer access. |
| `InnerList` | A parenthesised list of `StructuredFieldItem` entries, with its own `Parameters`. |
| `Parameters` | Ordered map of valid keys to non-null `BareItem` values. A present flag is `BooleanItem.True`; absence means no key. |
| `StructuredFieldMember` | Shared item-or-inner-list wrapper. Its parameters belong to the contained node, not a separate member-level collection. |

See [ownership and equality](/docs/structured-field-values/compatibility/#ownership-and-equality)
before sharing mutable nodes.

## DictionaryBuilder\<T\>

Configures mappings from an RFC 8941 Dictionary to POCO properties.

| Method | Description |
|--------|-------------|
| `.Member(key, x => x.Prop, type: ..., presence: ...)` | Maps a primitive property; type and presence arguments are optional. |
| `.InnerList(key, x => x.Prop, type: ..., presence: ...)` | Maps an `IReadOnlyList<TElement>?` of primitive elements. Optional by default. |
| `.InnerList(key, x => x.Prop, elementMapper)` | Maps a key to an `IReadOnlyList<TElement>?` property where each element is mapped by a nested `StructuredFieldMapper<TElement>`. |

Nested element mappers must be created with `Item`; passing a list or
dictionary mapper fails during configuration.

## ListBuilder\<T\>

Configures mappings from an RFC 8941 List to a POCO.

| Method | Description |
|--------|-------------|
| `.Elements(x => x.Prop, type: ...)` | Maps primitive elements to an `IReadOnlyList<TElement>` property; the wire type is optional. |
| `.Elements(x => x.Prop, elementMapper)` | Maps items with parameters using a nested item mapper. |

A null top-level collection serializes as an empty list. Null elements are
rejected, not dropped. This mapper does not support inner lists as list members.

## ItemBuilder\<T\>

Configures mappings from an RFC 8941 Item to a POCO.

| Method | Description |
|--------|-------------|
| `.Value(x => x.Prop, type: ...)` | Maps the required bare item value; the wire type is optional. |
| `.Parameter(paramKey, x => x.Prop, type: ..., presence: ...)` | Maps a parameter; wire type and presence are optional. |

Exactly one value mapping is required. A null item value throws on serialization,
even if its CLR property is nullable. Boolean flags require an explicit Boolean
value mapping; there are no placeholder values.
