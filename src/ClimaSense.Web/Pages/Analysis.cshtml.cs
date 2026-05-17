// SPDX-License-Identifier: MIT
//
// Slice-6 Analysis page. Pure-render PageModel: all data is fetched
// client-side via `/api/leaderboard`. See Analysis.cshtml for the
// HTML / JS surface.

using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ClimaSense.Web.Pages;

public sealed class AnalysisModel : PageModel
{
    public void OnGet()
    {
        // No server-side state. The page hydrates from XHR/fetch.
    }
}
