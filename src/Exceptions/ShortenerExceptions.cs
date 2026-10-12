namespace UrlShortener.Exceptions;

/// <summary>The short code does not exist. Maps to HTTP 404.</summary>
public class NotFoundException : Exception
{
    public NotFoundException(string code)
        : base($"No short URL exists for code '{code}'.")
    {
    }
}

/// <summary>The short code exists but its expiry time has passed. Maps to HTTP 410.</summary>
public class LinkExpiredException : Exception
{
    public LinkExpiredException(string code)
        : base($"The short URL '{code}' has expired.")
    {
    }
}

/// <summary>Optimistic locking failed: another request changed the row first. Maps to HTTP 409.</summary>
public class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(Exception inner)
        : base("The short URL was modified by another request. Reload it and try again.", inner)
    {
    }
}
