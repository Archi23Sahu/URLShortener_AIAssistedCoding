using FluentValidation;
using Microsoft.Extensions.Options;
using UrlShortener.Configurations;
using UrlShortener.DTOs;

namespace UrlShortener.Validators;

public class UpdateShortUrlRequestValidator : AbstractValidator<UpdateShortUrlRequest>
{
    public UpdateShortUrlRequestValidator(IOptions<ShortenerOptions> options)
    {
        var maxDays = options.Value.MaxExpiryDays;

        UrlRules.ApplyUrlRules(RuleFor(x => x.Url));

        RuleFor(x => x.ExpiryDays)
            .Must(days => days.HasValue && days.Value >= 1 && days.Value <= maxDays)
            .When(x => x.ExpiryDays.HasValue)
            .WithMessage($"ExpiryDays must be between 1 and {maxDays}.");
    }
}
