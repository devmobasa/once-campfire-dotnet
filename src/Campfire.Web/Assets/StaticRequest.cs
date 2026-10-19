namespace Campfire.Web.Assets;

/// <summary>The parts of a request <c>ActionDispatch::Static</c> looks at.</summary>
/// <param name="RawPath">The request path as sent (still percent-encoded), without the query string.</param>
public sealed record StaticRequest(
    string Method,
    string RawPath,
    string? AcceptEncoding = null,
    string? Range = null,
    string? IfModifiedSince = null,
    string? IfNoneMatch = null );
