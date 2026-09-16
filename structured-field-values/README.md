# DamianH.Http.StructuredFieldValues

RFC 9651 parser, serializer, and POCO mapper for HTTP Structured Field Values,
including Dates and Display Strings.

## Installation

Run in the consuming project directory:

```bash
dotnet add package DamianH.Http.StructuredFieldValues
```

## Quick start

```csharp
using DamianH.Http.StructuredFieldValues;

var priority = StructuredFieldParser.ParseDictionary("u=3, i");
string header = StructuredFieldSerializer.SerializeDictionary(priority);
// "u=3, i"
```

Use only for fields defined as Structured Fields, such as `Priority`, `Accept-CH`,
or a custom field with a declared structured type. `Cache-Control` is **not** a
Structured Field; use `System.Net.Http.Headers.CacheControlHeaderValue` instead.
`ToString()` is diagnostic-only: use the serializer or mapper for wire output.
The mapper is a projection, not a lossless editor; unknown members and parameters
are not retained.

## Documentation

- [Overview, parsing, serialization, POCO mapping, and samples](https://damianh.github.io/http-libs/docs/structured-field-values/overview/)
- [API reference](https://damianh.github.io/http-libs/docs/structured-field-values/api-reference/)
- [Type mapping and presence rules](https://damianh.github.io/http-libs/docs/structured-field-values/type-mapping/)
- [Ownership, equality, and breaking changes](https://damianh.github.io/http-libs/docs/structured-field-values/compatibility/)
