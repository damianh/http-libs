---
title: Structured Field Values type mapping
sidebarLabel: Type mapping
description: CLR type inference, explicit wire types, and required or optional mappings.
order: 3
section: Structured Field Values
---

# Structured Field Values type mapping

The mapper infers types from CLR property types. An explicit `type: ItemType.X`
selects another compatible wire representation; incompatible combinations fail
at mapper construction.

| CLR Type | Structured Type | Notes |
|----------|--------------|-------|
| `int`, `long` | Integer | Range: −999,999,999,999,999 to 999,999,999,999,999 |
| `decimal` | Decimal | Up to 12 integer digits and 3 fractional places |
| `bool` | Boolean | `?1` / `?0`; bare key in dictionaries = `?1` |
| `string` | String | Override with `type: ItemType.Token` or `ItemType.DisplayString` |
| `byte[]` | Byte Sequence | `:base64:` encoding |
| `long` with `type: ItemType.Date` | Date | Full-range Unix seconds; default `long` mapping remains Integer |

## Presence

Presence is configured independently using `MappingPresence` from
`DamianH.Http.StructuredFieldValues.Mapping`:

| Presence | Behavior |
|----------|----------|
| `Auto` (default) | Non-nullable value properties are required; nullable value and all reference properties are optional. Inner lists are optional. |
| `Required` | Missing members/parameters fail parsing; null properties fail serialization. |
| `Optional` | Missing members/parameters leave property initializers unchanged; null properties are omitted. |

C# reference-type nullable annotations do not change these defaults. An optional
non-nullable value property cannot distinguish absence from its default value.

```csharp
using DamianH.Http.StructuredFieldValues;
using DamianH.Http.StructuredFieldValues.Mapping;

public class EventMetadata
{
    public string Label { get; init; } = "";
    public string? Kind { get; init; }
    public long Timestamp { get; init; }

    public static readonly StructuredFieldMapper<EventMetadata> Mapper =
        StructuredFieldMapper<EventMetadata>.Dictionary(b => b
            .Member("label", x => x.Label,
                type: ItemType.DisplayString, presence: MappingPresence.Required)
            .Member("kind", x => x.Kind, type: ItemType.Token)
            .Member("at", x => x.Timestamp, type: ItemType.Date));
}
```

See the [API reference](/docs/structured-field-values/api-reference/) for mapper
factories, nested item mappings, and serialization constraints.
