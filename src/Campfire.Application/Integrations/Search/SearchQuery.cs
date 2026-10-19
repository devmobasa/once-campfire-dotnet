using System.Text;
using Campfire.Application.Ruby;


namespace Campfire.Application.Integrations.Search;

/// <summary>
/// <c>SearchesController#query</c> (<c>app/controllers/searches_controller.rb</c>):
/// <c>params[:q]&amp;.gsub(/[^[:word:]]/, " ")</c>. Every character that isn't one of Onigmo's Unicode word characters
/// (<see cref="OnigmoWord"/>) becomes one space. The search index quotes each resulting word before FTS5
/// <c>MATCH</c>, so <c>AND</c>, <c>OR</c> and <c>NOT</c> match literal terms.
/// </summary>
public static class SearchQuery
{
    /// <summary>The sanitized query, or null without a <c>q</c> parameter.</summary>
    public static string? Sanitize( string? rawQuery )
    {
        if ( rawQuery is null )
        {
            return null;
        }

        var output = new StringBuilder( rawQuery.Length );
        foreach ( var rune in rawQuery.EnumerateRunes( ) )
        {
            if ( OnigmoWord.Contains( rune ) )
            {
                output.Append( rune );
            }
            else
            {
                output.Append( ' ' );
            }
        }

        return output.ToString( );
    }

    /// <summary><c>query.present?</c>: not null and not only whitespace.</summary>
    public static bool IsPresent( string? query ) => !string.IsNullOrWhiteSpace( query );
}
