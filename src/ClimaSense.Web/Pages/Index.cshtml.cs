using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ClimaSense.Web.Pages;

/// <summary>
/// Slice-1 placeholder dashboard. Renders the heartbeat counter so the
/// SSE plumbing is observable end-to-end without a CLI tool. Slice 5
/// replaces this with the real Index.
/// </summary>
public sealed class IndexModel : PageModel
{
    public void OnGet()
    {
        // No state — all data comes from the SSE channel client-side.
    }
}
