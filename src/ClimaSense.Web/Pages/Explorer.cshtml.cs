// SPDX-License-Identifier: MIT
//
// Slice-4 historical Explorer page. Pure-render PageModel: all data is
// fetched client-side via /api/readings/range and /api/readings/heatmap.
// See Explorer.cshtml for the HTML/JS surface.

using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ClimaSense.Web.Pages;

public sealed class ExplorerModel : PageModel
{
    public void OnGet()
    {
        // No server-side state. The page hydrates from XHR/fetch.
    }
}
