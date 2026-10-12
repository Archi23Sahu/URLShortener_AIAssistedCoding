using FluentValidation;
using Microsoft.Extensions.Options;
using UrlShortener.Configurations;
using UrlShortener.DTOs;

namespace UrlShortener.Validators;

public class CreateShortUrlRequestValidator : AbstractValidator<CreateShortUrlRequest>
{
    public CreateShortUrlRequestValidator(IOptions<ShortenerOptions> options)
    {
        var maxDays = options.Value.MaxExpiryDays;

        UrlRules.ApplyUrlRules(RuleFor(x => x.Url));

        RuleFor(x => x.ExpiryDays)
            .Must(days => days.HasValue && days.Value >= 1 && days.Value <= maxDays)
            .When(x => x.ExpiryDays.HasValue)
            .WithMessage($"ExpiryDays must be between 1 and {maxDays}.");
    }
}
