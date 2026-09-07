using System.Text;

namespace PhoneTransfer.Domain;

// URI decoding belongs to the HTTP framework. Never decode again in storage.
public sealed record RelativeSharePath
{
    public string Value { get; }
    private RelativeSharePath(string value) => Value = value;

    public static RelativeSharePath Parse(string value, bool allowRoot = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 && allowRoot) return new(value);
        if (value.Length is 0 or > 4096 || !value.IsNormalized(NormalizationForm.FormC))
            throw new ArgumentException("Invalid relative path.", nameof(value));
        foreach (var segment in value.Split('/'))
        {
            if (segment.Length is 0 or > 255 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.Any(c => char.IsControl(c) || "\\:%<>\"|?*".Contains(c)))
                throw new ArgumentException("Invalid path component.", nameof(value));
            var stem = segment.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                    && "123456789¹²³".Contains(stem[3])))
                throw new ArgumentException("Reserved Windows path component.", nameof(value));
        }
        return new(value);
    }
}
