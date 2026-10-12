using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UrlShortener.DTOs;
using UrlShortener.RateLimiting;
using UrlShortener.Services;

namespace UrlShortener.Controllers;

[ApiController]
[EnableRateLimiting(RateLimitPolicies.Write)]
[Route("api/v1/shorten")]
[Produces("application/json")]
public class ShortenController : ControllerBase
{
    private readonly IUrlShortenerService _service;
    private readonly IValidator<CreateShortUrlRequest> _createValidator;
    private readonly IValidator<UpdateShortUrlRequest> _updateValidator;

    public ShortenController(
        IUrlShortenerService service,
        IValidator<CreateShortUrlRequest> createValidator,
        IValidator<UpdateShortUrlRequest> updateValidator)
    {
        _service = service;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
    }

    /// <summary>Creates a short URL. Returns the existing link (200) if the same URL is already active.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ShortUrlResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ShortUrlResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ShortUrlResponse>> Create([FromBody] CreateShortUrlRequest request, CancellationToken ct)
    {
        await _createValidator.ValidateAndThrowAsync(request, ct);

        var result = await _service.CreateAsync(request, ct);

        if (result.Created)
        {
            return Created(result.Response.ShortUrl, result.Response);
        }

        return Ok(result.Response);
    }

    /// <summary>Changes the target URL (and optionally resets the expiry) of an active link.</summary>
    [HttpPut(ApiRoutes.CodeSegment)]
    [ProducesResponseType(typeof(ShortUrlResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    public async Task<ActionResult<ShortUrlResponse>> Update(string code, [FromBody] UpdateShortUrlRequest request, CancellationToken ct)
    {
        await _updateValidator.ValidateAndThrowAsync(request, ct);

        var response = await _service.UpdateAsync(code, request, ct);
        return Ok(response);
    }

    /// <summary>Deletes a short URL and its click history.</summary>
    [HttpDelete(ApiRoutes.CodeSegment)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string code, CancellationToken ct)
    {
        await _service.DeleteAsync(code, ct);
        return NoContent();
    }
}
