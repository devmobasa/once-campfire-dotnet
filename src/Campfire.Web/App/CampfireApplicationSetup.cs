using Campfire.Application.Data;
using Campfire.Application.Data.Hooks;
using Campfire.Application.Memberships;
using Campfire.Application.Security;
using Campfire.Application.Storage;
using Campfire.Web.Assets;
using Campfire.Web.Cable;
using Campfire.Web.Channels;
using Campfire.Web.Hosting;
using Campfire.Web.Policy;
using Campfire.Web.Presenters;
using Campfire.Web.Security;
using Campfire.Web.Views.Caching;
using Campfire.Web.Workers;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Campfire.Web.App;

/// <summary>
/// The composition root of everything above the HTTP layer: data and its hooks, storage, the asset
/// pipeline and views, the cable server and the app's channels, the job workers, the policy services the HTTP layer
/// asks for, and the controllers. <see cref="BootCampfireAsync"/> runs the boot sequence before the host
/// listens.
/// </summary>
public static partial class CampfireApplicationSetup
{
    /// <summary>Registers the application. Call after <c>AddCampfireWeb</c>, whose options it reads.</summary>
    public static WebApplicationBuilder AddCampfireApplication( this WebApplicationBuilder builder )
    {
        ArgumentNullException.ThrowIfNull( builder );

        var services = builder.Services;
        var options = RegisteredOptions( services );
        var contentRoot = builder.Environment.ContentRootPath;

        // The framework's per-request lines stay out of the log at info (Rails logs one line per request). Warnings and
        // errors still go through.
        builder.Logging.AddFilter( "Microsoft.AspNetCore", LogLevel.Warning );

        services.AddSingleton( StoragePaths.From( options, contentRoot ) );
        AddSigners( services );
        AddData( services );
        AddStorage( services );

        services.AddCampfireAssets( );
        services.AddSingleton<LayoutAssets>( );
        services.AddSingleton<ReferenceFiles>( );
        services.AddSingleton( provider => new FragmentCache(
            options.FragmentCacheBytes,
            provider.GetRequiredService<TimeProvider>( ),
            provider.GetRequiredService<SignedGlobalIds>( ) ) );
        services.AddSingleton( provider => new PageResponseCache(
            options.ResponseCacheBytes,
            provider.GetRequiredService<TimeProvider>( ),
            provider.GetRequiredService<ILogger<PageResponseCache>>( ) ) );
        services.AddSingleton( provider => new PageAuthorityCache( logger: provider.GetRequiredService<ILogger<PageAuthorityCache>>( ) ) );
        services.AddSingleton<PageTicketMemo>( );
        services.AddSingleton( provider => new DatabaseGeneration(
            provider.GetRequiredService<CampfireDatabase>( ).Path,
            provider.GetRequiredService<FragmentCache>( ),
            provider.GetRequiredService<PageResponseCache>( ),
            provider.GetRequiredService<ILogger<DatabaseGeneration>>( ),
            provider.GetRequiredService<PageAuthorityCache>( ) ) );
        services.AddSingleton<ViewContexts>( );

        services.AddCampfireCable( )
            .AddTurboStreamsChannel( RoomStreamsAreAuthorized.IsGuardedStream )
            .AddAppChannels( );
        services.AddSingleton<SessionConnectionAuthenticator>( );
        services.AddSingleton<ICableConnectionAuthenticator>( provider => provider.GetRequiredService<SessionConnectionAuthenticator>( ) );
        services.AddSingleton<RoomBroadcasts>( );

        services.AddCampfireWorkers( );
        services.AddAppJobs( options.JobConcurrency );
        services.AddHostedService<StorageCleanupWorker>( );

        services.Replace( ServiceDescriptor.Singleton<IAuthenticationStore, AuthenticationStore>( ) );
        services.Replace( ServiceDescriptor.Singleton<IBanList, BanList>( ) );
        services.Replace( ServiceDescriptor.Singleton<IRoomScope, RoomScope>( ) );

        services.AddSingleton<CampfireApp>( );

        return builder;
    }

    /// <summary>
    /// The boot sequence, before the host listens: 1. <c>storage/{db,files}</c>; 2. the schema, loaded
    /// into an empty database or checked (<see cref="SchemaMismatchException"/> stops the boot); 3.
    /// <c>Membership.disconnect_all</c> (<c>config/puma.rb</c>), since no socket survived the last process.
    /// </summary>
    public static async Task<WebApplication> BootCampfireAsync( this WebApplication app )
    {
        ArgumentNullException.ThrowIfNull( app );

        var paths = app.Services.GetRequiredService<StoragePaths>( );
        paths.CreateDirectories( );

        var database = app.Services.GetRequiredService<CampfireDatabase>( );
        var disconnected = await database.WriteAsync( MembershipPresence.DisconnectAll, CancellationToken.None );

        var logger = app.Services.GetRequiredService<ILoggerFactory>( ).CreateLogger( "Campfire.Web.Boot" );
        LogBooted( logger, database.Path, database.Preparation, disconnected );

        return app;
    }

    private static void AddSigners( IServiceCollection services )
    {
        services.AddSingleton( provider => new SignedIds( provider.GetRequiredService<ITokenProtector>( ) ) );
        services.AddSingleton( provider => new SignedGlobalIds( provider.GetRequiredService<ITokenProtector>( ) ) );
        services.TryAddSingleton( provider => new TurboStreamNames( provider.GetRequiredService<ITokenProtector>( ) ) );
        services.AddSingleton( provider => new ActiveStorageVerifier( provider.GetRequiredService<ITokenProtector>( ) ) );
    }

    /// <summary>The database, opened on first use (the boot opens it), and the adapters behind its hooks.</summary>
    private static void AddData( IServiceCollection services )
    {
        services.AddSingleton<IMessageBodyText>( provider =>
            new RichTextBodyText( provider.GetRequiredService<SignedGlobalIds>( ), provider.GetRequiredService<TimeProvider>( ) ) );
        services.AddSingleton<StagedUploads>( );
        services.AddSingleton<JobHooks>( );
        services.AddSingleton<CableRemoteConnections>( );
        services.AddSingleton( provider =>
        {
            var jobs = provider.GetRequiredService<JobHooks>( );
            return new CampfireHooks(
                provider.GetRequiredService<IMessageBodyText>( ),
                provider.GetRequiredService<StagedUploads>( ),
                jobs,
                jobs,
                jobs,
                jobs,
                provider.GetRequiredService<CableRemoteConnections>( ) );
        } );
        services.AddSingleton( provider =>
        {
            var paths = provider.GetRequiredService<StoragePaths>( );
            paths.CreateDirectories( );
            var options = new DatabaseOptions
            {
                StorageRoot = paths.Root,
                Environment = paths.Environment,
                CreateMessageIndex = provider.GetRequiredService<CampfireOptions>( ).CreateMessageIndex,
                CreateUpdatedMessageIndex = provider.GetRequiredService<CampfireOptions>( ).CreateUpdatedMessageIndex
            };
            return CampfireDatabase.Open(
                options,
                provider.GetRequiredService<TimeProvider>( ),
                provider.GetRequiredService<CampfireHooks>( ),
                provider.GetRequiredService<ILoggerFactory>( ).CreateLogger<CampfireDatabase>( ) );
        } );
    }

    private static void AddStorage( IServiceCollection services )
    {
        services.AddSingleton<IBlobRecords, BlobRecords>( );
        services.AddSingleton( provider => new BlobStorage(
            new StorageOptions { Root = provider.GetRequiredService<StoragePaths>( ).Files },
            provider.GetRequiredService<ActiveStorageVerifier>( ),
            provider.GetRequiredService<IBlobRecords>( ),
            provider.GetRequiredService<TimeProvider>( ) ) );
    }

    /// <summary>The options <c>AddCampfireWeb</c> read from the environment and registered.</summary>
    private static CampfireOptions RegisteredOptions( IServiceCollection services ) =>
        services.LastOrDefault( descriptor => descriptor.ServiceType == typeof( CampfireOptions ) )?.ImplementationInstance as CampfireOptions
        ?? throw new InvalidOperationException( "Call AddCampfireWeb before AddCampfireApplication." );

    [LoggerMessage( Level = LogLevel.Information, Message = "Booted on {Database} ({Preparation}); {Disconnected} memberships disconnected" )]
    private static partial void LogBooted( ILogger logger, string database, SchemaPreparation preparation, int disconnected );
}
