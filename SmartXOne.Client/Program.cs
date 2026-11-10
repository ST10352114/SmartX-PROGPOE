// Code Attribution
// Purpose: Dashboard layout and design approach for the client
// Author: Yigitbasioglu, O.M. and Velcu, O.
// Title: A review of dashboards in performance management: Implications for design and research
// Year: 2012
// URL/DOI: https://doi.org/10.1016/j.accinf.2011.08.002
// Accessed: 10 November 2026

using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SmartXOne.Client;
using SmartXOne.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The API runs on a different origin than the Blazor app during development.
// wwwroot/appsettings.json supplies its base URL so the same build works both
// locally (VS multi-startup) and in Docker (where the value is overridden).
var apiBaseUrl = builder.Configuration["ApiBaseUrl"]
                 ?? throw new InvalidOperationException("ApiBaseUrl is not configured in wwwroot/appsettings.json.");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBaseUrl) });
builder.Services.AddScoped<SmartXApiClient>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<CommandStreamState>();
builder.Services.AddScoped<IngestionState>();
builder.Services.AddScoped<TopologyState>();

await builder.Build().RunAsync();
