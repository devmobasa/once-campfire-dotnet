using Campfire.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;

namespace Campfire.IntegrationTests.Slice;

public sealed class MessageIndexConfigurationTests
{
    [Theory]
    [InlineData( null, false )]
    [InlineData( "false", false )]
    [InlineData( "true", true )]
    [InlineData( "1", true )]
    public async Task The_optional_update_index_is_created_only_when_explicitly_enabled( string? setting, bool expected )
    {
        await using var factory = new IndexAppFactory( setting );
        _ = factory.App;

        Assert.Equal( expected ? 1L : 0L, factory.Scalar<long>( "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='index_messages_on_room_id_and_updated_at'" ) );
        Assert.Equal( 1L, factory.Scalar<long>( "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='index_messages_on_room_id_and_created_at'" ) );
    }

    private sealed class IndexAppFactory( string? setting ) : SliceAppFactory( loadDatabase: null )
    {
        protected override void ConfigureWebHost( IWebHostBuilder builder )
        {
            base.ConfigureWebHost( builder );
            if ( setting is not null )
            {
                builder.UseSetting( "CAMPFIRE_MESSAGE_UPDATED_INDEX", setting );
            }
        }
    }
}
