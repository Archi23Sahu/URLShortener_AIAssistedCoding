using FluentValidation;
using UrlShortener.Services;

namespace UrlShortener.Validators;

/// <summary>Rule shared by the create and update request validators.</summary>
public static class UrlRules
{
    public const int MaxUrlLength = 2048;

    public static void ApplyUrlRules<T>(IRuleBuilderInitial<T, string> rule) =>
        rule.Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Url is required.")
            .Must(url => UrlNormalizer.TryNormalize(url, out var normalized) && normalized.Length <= MaxUrlLength)
            .WithMessage($"Url must be an absolute http or https URL of at most {MaxUrlLength} characters and must not contain credentials.");
}
