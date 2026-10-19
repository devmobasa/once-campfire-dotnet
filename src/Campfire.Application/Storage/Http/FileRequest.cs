namespace Campfire.Application.Storage.Http;

/// <summary>File response inputs. The HTTP layer supplies conditional validation over the file's Last-Modified value.</summary>
public sealed record FileRequest( string Method, string? Range = null, Func<string, bool>? IsFresh = null );
