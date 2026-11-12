// Copyright (c) Duende Software. All rights reserved.
// See LICENSE in the project root for license information.

namespace DamianH.Http.StructuredFieldValues;

/// <summary>
/// Represents a byte sequence item in a structured field value.
/// RFC 8941 encodes byte sequences as base64 between colons (e.g., :aGVsbG8=:).
/// </summary>
public sealed class ByteSequenceItem : BareItem
{
    private readonly byte[] _value;

    /// <summary>
    /// Initializes a new instance of the <see cref="ByteSequenceItem"/> class.
    /// </summary>
    /// <param name="value">The byte array value.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when value is null.
    /// </exception>
    public ByteSequenceItem(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = (byte[])value.Clone();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ByteSequenceItem"/> class from a base64 string.
    /// </summary>
    /// <param name="base64Value">The base64-encoded value.</param>
    /// <returns>A new ByteSequenceItem.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when base64Value is null.
    /// </exception>
    /// <exception cref="FormatException">
    /// Thrown when base64Value is not valid base64.
    /// </exception>
    public static ByteSequenceItem FromBase64(string base64Value)
    {
        ArgumentNullException.ThrowIfNull(base64Value);
        var bytes = Convert.FromBase64String(ClearPadBits(base64Value));
        return new ByteSequenceItem(bytes);
    }

    // RFC 9651 section 4.2.7: parsers SHOULD NOT fail when non-zero pad bits are present.
    // .NET 11 Convert.FromBase64String rejects them, so clear them before decoding.
    private static string ClearPadBits(string base64Value)
    {
        // Convert.FromBase64String ignores these, so drop them to locate the padding.
        if (base64Value.AsSpan().IndexOfAny(" \t\r\n") >= 0)
        {
            base64Value = string.Concat(base64Value.Where(c => c is not (' ' or '\t' or '\r' or '\n')));
        }
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var padding = base64Value.EndsWith("==", StringComparison.Ordinal) ? 2
            : base64Value.EndsWith('=') ? 1
            : 0;
        var lastDataIndex = base64Value.Length - padding - 1;
        if (padding == 0 || lastDataIndex < 0)
        {
            return base64Value;
        }
        var index = Alphabet.IndexOf(base64Value[lastDataIndex]);
        var mask = padding == 2 ? 0b11_0000 : 0b11_1100;
        if (index < 0 || (index & mask) == index)
        {
            return base64Value;
        }
        return string.Concat(base64Value.AsSpan(0, lastDataIndex), Alphabet.AsSpan(index & mask, 1), base64Value.AsSpan(lastDataIndex + 1));
    }

    /// <summary>
    /// Gets a defensive copy of the byte array value.
    /// </summary>
    public byte[] ByteArrayValue => ToArray();

    /// <summary>Gets read-only access to the immutable bytes.</summary>
    public ReadOnlySpan<byte> Bytes => _value;

    /// <summary>Returns a new copy of the bytes.</summary>
    public byte[] ToArray() => (byte[])_value.Clone();

    /// <summary>
    /// Gets the base64-encoded representation of the byte sequence.
    /// </summary>
    public string Base64Value => Convert.ToBase64String(_value);

    /// <inheritdoc/>
    public override object Value => ToArray();

    /// <inheritdoc/>
    public override ItemType Type => ItemType.ByteSequence;

    /// <inheritdoc/>
    public override string ToString() => $":{Base64Value}:";

    /// <inheritdoc/>
    public override bool Equals(object? obj) =>
        obj is ByteSequenceItem other && _value.SequenceEqual(other._value);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var b in _value)
        {
            hash.Add(b);
        }
        return hash.ToHashCode();
    }

}
