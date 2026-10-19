using Campfire.Application.Storage.Media;

namespace Campfire.Application.Storage;

/// <summary>Variants and previews: <c>blob.variant</c>, <c>blob.preview</c> and <c>blob.representation</c>, each <c>.processed</c>.</summary>
public sealed partial class BlobStorage
{
    /// <summary>
    /// <c>previewable?</c>: only the video previewer can accept (the image has no poppler or mupdf, so PDFs aren't
    /// previewable either).
    /// </summary>
    public async Task<bool> IsPreviewableAsync( Blob blob )
    {
        ArgumentNullException.ThrowIfNull( blob );

        return blob.IsVideo && await Tools.FfmpegExistsAsync( );
    }

    /// <summary><c>representable?</c>.</summary>
    public async Task<bool> IsRepresentableAsync( Blob blob ) => blob.IsVariable || await IsPreviewableAsync( blob );

    /// <summary>
    /// <c>blob.variant(transformations)</c>'s variation: defaulted to the blob's variant format
    /// (<see cref="StorageFailure.Invariable"/> otherwise).
    /// </summary>
    public static Variation VariationFor( Blob blob, Variation transformations )
    {
        ArgumentNullException.ThrowIfNull( blob );
        ArgumentNullException.ThrowIfNull( transformations );

        if ( !blob.IsVariable )
        {
            throw new StorageException( StorageFailure.Invariable, $"Can't transform blob with ID={blob.Id} and content_type={blob.ContentType}" );
        }

        return transformations.DefaultTo( "format", blob.DefaultVariantFormat( ) );
    }

    /// <summary>The image blob of the variant, if <c>variant_records</c> already has it.</summary>
    public Task<Blob?> ExistingVariantAsync( Blob blob, Variation variation, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( blob );
        ArgumentNullException.ThrowIfNull( variation );

        return _records.VariantImageAsync( blob.Id, variation.Digest( ), cancellationToken );
    }

    /// <summary>
    /// The file half of <c>VariantWithRecord#processed</c> for a defaulted variation (see
    /// <see cref="VariationFor"/>): transforms a copy of the blob with libvips into a staged image
    /// named <c>&lt;base&gt;.&lt;format&gt;</c>, already analyzed (Rails analyzes it in an
    /// <c>AnalyzeJob</c> after commit; the row ends up the same).
    /// </summary>
    public async Task<StagedBlob> TransformVariantAsync( Blob blob, Variation variation, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( blob );
        ArgumentNullException.ThrowIfNull( variation );

        var format = variation.Format( );
        var operations = VariantTransformer.Operations( variation );
        var filename = new BlobFilename( $"{blob.Filename.Base}.{format.ToLowerInvariant( )}" );

        // The saver comes from this suffix, as from image_processing's "image_processing<random>.<format>"
        // tempfile. Format() admits only Marcel's extensions ([a-z0-9._+-], any case), so it is path-safe.
        var output = Disk.NewStagingPath( "." + format );

        await _conversions.WaitAsync( cancellationToken );
        try
        {
            using var input = await OpenAsync( blob, cancellationToken );
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
            deadline.CancelAfter( _options.ImageTimeout );
            await Task.Run(
                ( ) => VipsImages.Transform( input.Path, output, operations, _options.MaxImagePixels, deadline.Token ),
                CancellationToken.None );
        }
        catch ( OperationCanceledException ) when ( !cancellationToken.IsCancellationRequested )
        {
            File.Delete( output );
            throw new StorageException(
                StorageFailure.TimedOut,
                $"libvips took longer than {_options.ImageTimeout.TotalSeconds:0.###} s on blob {blob.Id} and was stopped." );
        }
        catch
        {
            File.Delete( output );
            throw;
        }
        finally
        {
            _conversions.Release( );
        }

        return await StageGeneratedAsync( output, filename, variation.ContentType( ), blob.ServiceName, cancellationToken );
    }

    /// <summary>
    /// <c>VariantWithRecord#processed</c>: the recorded variant when there is one, else transforms,
    /// publishes the image and records it. When another request records the same variation first,
    /// this one's file is deleted and theirs is returned.
    /// </summary>
    public async Task<Blob> ProcessVariantAsync( Blob blob, Variation variation, CancellationToken cancellationToken )
    {
        _variantFlights.ThrowIfDisposed( );
        if ( await ExistingVariantAsync( blob, variation, cancellationToken ) is { } existing )
        {
            return existing;
        }

        using var flight = await _variantFlights.EnterAsync( blob.Id, variation.Digest( ), cancellationToken );
        if ( await ExistingVariantAsync( blob, variation, cancellationToken ) is { } recordedEarlier )
        {
            return recordedEarlier;
        }

        using var staged = await TransformVariantAsync( blob, variation, cancellationToken );
        staged.Publish( );
        var recorded = await _records.RecordVariantAsync( blob.Id, variation.Digest( ), staged.Blob, Now( ), cancellationToken );
        if ( recorded is not null )
        {
            staged.Keep( );
            return recorded;
        }

        return await ExistingVariantAsync( blob, variation, cancellationToken )
            ?? throw new StorageException(
                StorageFailure.FileNotFound,
                $"The variant {variation} of blob {blob.Id} was recorded elsewhere but can't be found." );
    }

    /// <summary><c>blob.preview_image</c>, if it has been generated.</summary>
    public Task<Blob?> ExistingPreviewImageAsync( Blob blob, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( blob );

        return _records.AttachedBlobAsync( AttachmentOwner.ForBlob( blob.Id ), BlobRecords.PreviewImageName, cancellationToken );
    }

    /// <summary>
    /// The file half of <c>Preview#process</c>: ffmpeg draws the video's relevant frame, staged as
    /// <c>&lt;base&gt;.jpg</c> (<c>image/jpeg</c>) and already analyzed.
    /// </summary>
    public async Task<StagedBlob> DrawPreviewImageAsync( Blob blob, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( blob );

        if ( !await IsPreviewableAsync( blob ) )
        {
            throw new StorageException( StorageFailure.Unpreviewable, $"No previewer found for blob with ID={blob.Id} and content_type={blob.ContentType}" );
        }

        var output = Disk.NewStagingPath( ".jpg" );
        await _conversions.WaitAsync( cancellationToken );
        try
        {
            using var input = await OpenAsync( blob, cancellationToken );
            var frame = StorageFiles.CreateNew( output );
            await using ( frame )
            {
                await Tools.DrawPreviewAsync( input.Path, frame, cancellationToken );
            }
        }
        catch
        {
            File.Delete( output );
            throw;
        }
        finally
        {
            _conversions.Release( );
        }

        return await StageGeneratedAsync( output, new BlobFilename( $"{blob.Filename.Base}.jpg" ), "image/jpeg", blob.ServiceName, cancellationToken );
    }

    /// <summary>
    /// <c>blob.preview_image</c>, drawing and attaching it when missing (<c>Preview#process</c>). When
    /// another request attaches one first, this one's file is deleted and theirs is returned.
    /// </summary>
    public async Task<Blob> PreviewImageAsync( Blob blob, CancellationToken cancellationToken )
    {
        if ( await ExistingPreviewImageAsync( blob, cancellationToken ) is { } existing )
        {
            return existing;
        }

        using var staged = await DrawPreviewImageAsync( blob, cancellationToken );
        staged.Publish( );
        var recorded = await _records.RecordPreviewImageAsync( blob.Id, staged.Blob, Now( ), cancellationToken );
        if ( recorded is not null )
        {
            staged.Keep( );
            return recorded;
        }

        return await ExistingPreviewImageAsync( blob, cancellationToken )
            ?? throw new StorageException( StorageFailure.FileNotFound, $"The preview image of blob {blob.Id} was attached elsewhere but can't be found." );
    }

    /// <summary>
    /// <c>blob.preview(transformations).processed</c>, returning the blob to serve: the preview image
    /// itself for empty transformations, otherwise its processed variant.
    /// </summary>
    public async Task<Blob> ProcessPreviewAsync( Blob blob, Variation transformations, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( transformations );

        var image = await PreviewImageAsync( blob, cancellationToken );

        if ( transformations.IsEmpty )
        {
            return image;
        }

        return await ProcessVariantAsync( image, VariationFor( image, transformations ), cancellationToken );
    }

    /// <summary>
    /// <c>blob.representation(transformations).processed</c>: a preview for a previewable blob, a
    /// variant for a variable one, <see cref="StorageFailure.Unrepresentable"/> otherwise.
    /// </summary>
    public async Task<Blob> ProcessRepresentationAsync( Blob blob, Variation transformations, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( blob );

        if ( await IsPreviewableAsync( blob ) )
        {
            return await ProcessPreviewAsync( blob, transformations, cancellationToken );
        }

        if ( blob.IsVariable )
        {
            return await ProcessVariantAsync( blob, VariationFor( blob, transformations ), cancellationToken );
        }

        throw new StorageException(
            StorageFailure.Unrepresentable,
            $"No previewer found and can't transform blob with ID={blob.Id} and content_type={blob.ContentType}" );
    }

    /// <summary>
    /// Stages a generated image as attaching <c>{ io:, filename:, content_type: }</c> would build it
    /// (Marcel identifies it; <c>identified: true</c>), plus the metadata its analysis would save.
    /// </summary>
    private async Task<StagedBlob> StageGeneratedAsync(
        string output, BlobFilename filename, string contentType, string serviceName, CancellationToken cancellationToken )
    {
        var file = await Disk.AdoptStagedAsync( output, _options.MaxUploadBytes, cancellationToken );
        try
        {
            var blob = NewBlob.Unfurl( BlobKeys.Generate( ), file.Head.Span, file.Length, file.Checksum, filename, contentType, serviceName );
            var extracted = await AnalyzeFileAsync( BlobAnalyzer.For( blob.ContentType ), file.Path, cancellationToken );

            return new StagedBlob( Disk, blob with
            {
                Metadata = blob.Metadata.Merge( extracted.With( BlobMetadata.AnalyzedKey, true ) )
            }, file );
        }
        catch
        {
            file.Dispose( );
            throw;
        }
    }
}
