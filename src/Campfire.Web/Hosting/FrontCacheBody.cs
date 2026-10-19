using System.Buffers;
using System.IO.Pipelines;
using Campfire.Web.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace Campfire.Web.Hosting;

/// <summary>Captures only eligible public bytes while forwarding them immediately; large and private bodies stream.</summary>
internal sealed class FrontCacheBody( IHttpResponseBodyFeature inner, HttpContext httpContext, int maximumBytes ) : Stream, IHttpResponseBodyFeature
{
    /// <summary>The read size when a captured response sends a file.</summary>
    private const int FileChunkBytes = 16 * 1024;

    private MemoryStream? _capture;
    private PipeWriter? _writer;
    private bool _overflowed;
    private bool _suppressBody;
    public TimeSpan? Lifetime { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Stream Stream => this;
    public PipeWriter Writer => _writer ??= PipeWriter.Create( this, new StreamPipeWriterOptions( leaveOpen: true ) );
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException( );
    public override long Position
    {
        get => throw new NotSupportedException( ); set => throw new NotSupportedException( );
    }

    public Task DecideAsync( )
    {
        CreatedAt = DateTimeOffset.UtcNow;
        if ( httpContext.Response.StatusCode == StatusCodes.Status200OK
            && RackConditionalGet.IsFresh( httpContext.Request.Headers, httpContext.Response.Headers.ETag.ToString( ), httpContext.Response.Headers.LastModified.ToString( ) ) )
        {
            httpContext.Response.StatusCode = StatusCodes.Status304NotModified;
            httpContext.Response.Headers.ContentLength = null;
            httpContext.Response.ContentType = null;
            _suppressBody = true;
        }

        Lifetime = FrontResponseCache.Lifetime( httpContext.Response );
        if ( Lifetime is not null )
        {
            httpContext.Response.Headers.Remove( HeaderNames.SetCookie );
            _capture = new MemoryStream( );
        }

        return Task.CompletedTask;
    }

    public void DisableBuffering( ) => inner.DisableBuffering( );
    public Task StartAsync( CancellationToken cancellationToken = default ) => inner.StartAsync( cancellationToken );
    public Task CompleteAsync( ) => inner.CompleteAsync( );

    public override async ValueTask WriteAsync( ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default )
    {
        await inner.StartAsync( cancellationToken );
        if ( _suppressBody )
        {
            return;
        }

        if ( _capture is not null && !_overflowed )
        {
            if ( buffer.Length > maximumBytes - _capture.Length )
            {
                _overflowed = true;
                _capture.Dispose( );
                _capture = null;
            }
            else
            {
                _capture.Write( buffer.Span );
            }
        }

        await inner.Stream.WriteAsync( buffer, cancellationToken );
    }

    public override Task WriteAsync( byte[] buffer, int offset, int count, CancellationToken cancellationToken ) =>
        WriteAsync( buffer.AsMemory( offset, count ), cancellationToken ).AsTask( );

    public override void Write( byte[] buffer, int offset, int count ) => throw new InvalidOperationException( "Use asynchronous response writes." );
    public override Task FlushAsync( CancellationToken cancellationToken ) => inner.Stream.FlushAsync( cancellationToken );
    public override void Flush( ) => throw new InvalidOperationException( "Use asynchronous response flushing." );

    public async Task SendFileAsync( string path, long offset, long? count, CancellationToken cancellationToken = default )
    {
        await inner.StartAsync( cancellationToken );
        if ( _suppressBody )
        {
            return;
        }

        if ( Lifetime is null || count > maximumBytes )
        {
            _overflowed = count > maximumBytes;
            await inner.SendFileAsync( path, offset, count, cancellationToken );
            return;
        }

        const FileOptions sequentialReads = FileOptions.Asynchronous | FileOptions.SequentialScan;
        await using var file = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileChunkBytes, sequentialReads );
        file.Seek( offset, SeekOrigin.Begin );
        var remaining = count ?? file.Length - offset;
        var buffer = ArrayPool<byte>.Shared.Rent( FileChunkBytes );
        try
        {
            while ( remaining > 0 )
            {
                var read = await file.ReadAsync( buffer.AsMemory( 0, ( int ) Math.Min( buffer.Length, remaining ) ), cancellationToken );
                if ( read == 0 )
                {
                    break;
                }

                await WriteAsync( buffer.AsMemory( 0, read ), cancellationToken );
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return( buffer, clearArray: true );
        }
    }

    public async Task<byte[]?> CapturedAsync( CancellationToken cancellationToken )
    {
        if ( _writer is not null )
        {
            await _writer.FlushAsync( cancellationToken );
        }

        return _overflowed ? null : _capture?.ToArray( );
    }

    protected override void Dispose( bool disposing )
    {
        if ( disposing )
        {
            _capture?.Dispose( );
        }

        base.Dispose( disposing );
    }

    public override int Read( byte[] buffer, int offset, int count ) => throw new NotSupportedException( );
    public override long Seek( long offset, SeekOrigin origin ) => throw new NotSupportedException( );
    public override void SetLength( long value ) => throw new NotSupportedException( );
}
