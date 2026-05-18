// SPDX-License-Identifier: MIT
//
// Slice-11 Alert history page. Pure-render PageModel: the rows table
// is fetched client-side from `/api/alerts` and the live toast feed
// subscribes to the slice-1 `/api/alerts/stream` SSE endpoint
// (listening for the slice-11 `breach-detected` event type).
//
// Per the brief: the toast handler is page-local so it appears even
// when reviewers are on the Alerts page itself; the dashboard's
// equivalent handler in `Index.cshtml` keeps the SSE-driven UX
// consistent across the whole site.

using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ClimaSense.Web.Pages;

public sealed class AlertsModel : PageModel
{
    public void OnGet()
    {
        // No server-side state. The page hydrates from /api/alerts +
        // /api/alerts/rules and subscribes to /api/alerts/stream.
    }
}
