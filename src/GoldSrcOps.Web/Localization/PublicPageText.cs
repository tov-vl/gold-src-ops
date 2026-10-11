using System.Globalization;
using System.Resources;

namespace GoldSrcOps.Web.Localization;

public sealed class PublicPageText
{
    private static readonly ResourceManager Resources = new("GoldSrcOps.Web.Localization.PublicPages", typeof(PublicPageText).Assembly);
    private static readonly PublicPageText Russian = new("ru");
    private static readonly PublicPageText English = new("en");
    private readonly CultureInfo _culture;

    private PublicPageText(string code)
    {
        Code = code;
        _culture = CultureInfo.GetCultureInfo(code);
    }

    public string Code { get; }

    public static PublicPageText For(string? language) => string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
        ? English : Russian;

    public string this[string key] => Resources.GetString(key, _culture)
        ?? throw new InvalidOperationException($"Missing public page text: {key}");

    public string Link(string path) => string.Equals(Code, "en", StringComparison.Ordinal) ? $"{path}{(path.Contains('?', StringComparison.Ordinal) ? "&" : "?")}lang=en" : path;

    public string Format(string key, params object[] arguments) => string.Format(CultureInfo.InvariantCulture, this[key], arguments);

    public string Age(double seconds) => string.Format(CultureInfo.InvariantCulture, this["Age"], Math.Ceiling(seconds));
}
