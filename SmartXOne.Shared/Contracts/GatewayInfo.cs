// Source attribution: Microsoft .NET Class Libraries Documentation
// URL: https://learn.microsoft.com/en-us/dotnet/standard/class-libraries

namespace SmartXOne.Shared.Contracts;

/// <summary>
/// Small payload returned by the gateway's health/identity endpoint.
/// Used in Phase 1 purely to prove the Client -> API -> Shared round trip works
/// end to end (the Client deserialises this exact type that the API serialised).
/// </summary>
/// <param name="Service">Friendly name of the running service.</param>
/// <param name="Version">Assembly / build version string.</param>
/// <param name="Environment">ASP.NET Core environment name (Development, Production...).</param>
/// <param name="ServerTimeUtc">Server clock at the moment the request was handled.</param>
/// <param name="Message">Human-readable confirmation message shown on the landing page.</param>
public record GatewayInfo(
    string Service,
    string Version,
    string Environment,
    DateTimeOffset ServerTimeUtc,
    string Message);
