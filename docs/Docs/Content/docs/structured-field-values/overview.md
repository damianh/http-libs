---
title: Structured Field Values overview
sidebarLabel: Overview
description: Install and use the RFC 9651 parser, serializer, and POCO mapper.
order: 1
section: Structured Field Values
---

# Structured Field Values overview

`DamianH.Http.StructuredFieldValues` is an RFC 9651 parser, serializer, and POCO
mapper for HTTP Structured Field Values, including the six original RFC 8941
types plus Dates and Display Strings.

Use this library only for fields defined as Structured Fields, such as `Priority`
and `Accept-CH`, or custom fields with a declared structured type. `Cache-Control`
is **not** a Structured Field; use `System.Net.Http.Headers.CacheControlHeaderValue`
for its grammar.

## Installation

Run in the consuming project directory:

```bash
dotnet add package DamianH.Http.StructuredFieldValues
```

## Quick Start

### Parsing

```csharp
using DamianH.Http.StructuredFieldValues;

// Parse a Priority structured dictionary
StructuredFieldDictionary dict = StructuredFieldParser.ParseDictionary("u=3, i");
// dict["u"].Item.Value is IntegerItem(3)
// dict["i"].Item.Value is BooleanItem(true)  (bare key = ?1)

// Parse a structured list
StructuredFieldList list = StructuredFieldParser.ParseList("a, b, c");

// Parse a single item
StructuredFieldItem item = StructuredFieldParser.ParseItem("42");

var eventTime = StructuredFieldParser.ParseItem("@1659578233");
// ((DateItem)eventTime.Value).UnixSeconds == 1659578233

var label = StructuredFieldParser.ParseItem("%\"caf%c3%a9\"");
// label.Value is DisplayStringItem, containing Unicode text
```

### Serializing

```csharp
var dict = new StructuredFieldDictionary
{
    ["u"] = StructuredFieldMember.FromItem(new IntegerItem(3)),
    ["i"] = StructuredFieldMember.FromItem(BooleanItem.True)
};

string header = StructuredFieldSerializer.SerializeDictionary(dict);
// "u=3, i"

var item = new StructuredFieldItem(new TokenItem("gzip"));
item.Parameters.Add("enabled", BooleanItem.True);
string member = StructuredFieldSerializer.SerializeItem(item);
// "gzip;enabled"
```

**`ToString()` is diagnostic-only, not wire output.** Always use
`StructuredFieldSerializer` or the mapper's `Serialize` method when writing
headers. Diagnostic strings need not be quoted, complete, canonical, or
round-trippable.

### POCO Mapping

The mapper converts structured values to and from plain C# objects. Mappers
snapshot their configuration and are reusable across threads; store them in
`static readonly` fields. Mapped models must be classes with a public
parameterless constructor and directly accessible readable/writable properties
(including `init` properties). Concurrent mutation of the same model is not supported.

```csharp
// Define a POCO
public class PriorityHeader
{
    public int? Urgency { get; init; }
    public bool? Incremental { get; init; }

    // Define the mapper once, store statically
    public static readonly StructuredFieldMapper<PriorityHeader> Mapper =
        StructuredFieldMapper<PriorityHeader>.Dictionary(b => b
            .Member("u", x => x.Urgency)
            .Member("i", x => x.Incremental));
}

// Parse
var priority = PriorityHeader.Mapper.Parse("u=3, i");
// priority.Urgency == 3, priority.Incremental == true

// Serialize
string header = PriorityHeader.Mapper.Serialize(new PriorityHeader { Urgency = 3, Incremental = true });
// "u=3, i"

// Try-parse (returns false for malformed input instead of throwing)
if (PriorityHeader.Mapper.TryParse(request.Headers["Priority"], out var p))
{
    // use p
}
```

See the [API reference](/docs/structured-field-values/api-reference/) for parser,
serializer, mapper, and builder contracts, [type mapping](/docs/structured-field-values/type-mapping/)
for presence rules, and [compatibility](/docs/structured-field-values/compatibility/)
for ownership, equality, and breaking changes.

## Samples

- [`samples/HttpClientSample`](https://github.com/damianh/http-libs/tree/main/structured-field-values/samples/HttpClientSample) — `HttpClient` integration for `Priority` (RFC 9218), with helpers for reading and writing structured headers on `HttpRequestMessage` and `HttpResponseMessage`.

- [`samples/AspNetCoreSample`](https://github.com/damianh/http-libs/tree/main/structured-field-values/samples/AspNetCoreSample) — ASP.NET Core integration for `Priority`, `Accept-CH`, and a custom token-list field.

> **Note**: The sample projects target .NET 10 with `LangVersion=preview` and use C# 14 extension declaration syntax.
