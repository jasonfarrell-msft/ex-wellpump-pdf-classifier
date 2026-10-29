using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace WellpumpClassifier;

public sealed class IdentityDiagnosticsSettings
{
    public string? ExpectedPrincipalId { get; set; }
    public string? ExpectedTenantId { get; set; }
}

public enum DiagnosticAudience
{
    ExpectedCognitiveServices,
    Unexpected,
    MissingOrMalformed
}

// Local inspection only: this does NOT validate a JWT or participate in authorization.
// Disabled by default; only the token actually attached to a rejected DI request is passed here.
public sealed class TokenDiagnostics(TokenCredential credential, IHostEnvironment environment,
    IdentityDiagnosticsSettings settings, ILogger<TokenDiagnostics> logger, TimeProvider clock)
{
    private int emitted;

    public void ObserveUnauthorized(AccessToken token, HttpResponseHeaders headers)
    {
        if (credential is not ManagedIdentityCredential || environment.IsDevelopment()
            || !Guid.TryParse(settings.ExpectedPrincipalId, out var expectedPrincipal)) return;
        if (Interlocked.Exchange(ref emitted, 1) != 0) return;
        try
        {
            Guid? expectedTenant = Guid.TryParse(settings.ExpectedTenantId, out var tenant) ? tenant : null;
            var summary = Inspect(token.Token, expectedPrincipal, expectedTenant);
            var requestId = RequestId(headers, "apim-request-id") ?? RequestId(headers, "x-ms-request-id");
            // Never pass token, claims, expected IDs, unexpected audience, headers or exceptions
            // to the logger. Only booleans/enums, UTC time and a canonical service GUID survive.
            logger.LogWarning(new EventId(4102, "DocumentIntelligenceIdentityDiagnostic"),
                "DI 401 identity diagnostic at {UtcTime}: OidPresentAndValid={OidPresentAndValid}; " +
                "OidMatchesExpected={OidMatchesExpected}; TenantMatchesExpected={TenantMatchesExpected}; " +
                "Audience={Audience}; ServiceRequestId={ServiceRequestId}.",
                clock.GetUtcNow(), summary.OidPresentAndValid, summary.OidMatchesExpected,
                summary.TenantMatchesExpected, summary.Audience, requestId);
        }
        catch
        {
            // Best-effort diagnostics must not change the original authorization failure,
            // and parser/provider errors must not expose claims through an exception log.
        }
    }

    private sealed record Summary(bool OidPresentAndValid, bool OidMatchesExpected,
        bool TenantMatchesExpected, DiagnosticAudience Audience);

    private static Summary Inspect(string? token, Guid expectedPrincipal, Guid? expectedTenant)
    {
        var malformed = new Summary(false, false, false, DiagnosticAudience.MissingOrMalformed);
        if (token is not { Length: > 0 and <= 32768 }) return malformed;
        var segments = token.Split('.');
        if (segments.Length != 3 || segments[0].Length == 0 || segments[2].Length == 0
            || segments[1].Length is < 1 or > 16384) return malformed;
        try
        {
            var payload = WebEncoders.Base64UrlDecode(segments[1]);
            using var json = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return malformed;
            var oidValid = ClaimGuid(root, "oid", out var oid);
            var tenantValid = ClaimGuid(root, "tid", out var tenant);
            var audience = DiagnosticAudience.MissingOrMalformed;
            if (root.TryGetProperty("aud", out var aud) && aud.ValueKind == JsonValueKind.String
                && aud.GetString() is { Length: > 0 and <= 2048 } value)
                audience = value is "https://cognitiveservices.azure.com" or "https://cognitiveservices.azure.com/"
                    ? DiagnosticAudience.ExpectedCognitiveServices : DiagnosticAudience.Unexpected;
            return new(oidValid, oidValid && oid == expectedPrincipal,
                tenantValid && expectedTenant is not null && tenant == expectedTenant.Value, audience);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or JsonException or InvalidOperationException)
        {
            return malformed;
        }
    }

    private static bool ClaimGuid(JsonElement root, string claim, out Guid value)
    {
        value = default;
        return root.TryGetProperty(claim, out var item) && item.ValueKind == JsonValueKind.String
            && Guid.TryParse(item.GetString(), out value);
    }

    private static Guid? RequestId(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values)) return null;
        var candidates = values.Take(2).ToArray();
        return candidates.Length == 1 && Guid.TryParseExact(candidates[0], "D", out var id) ? id : null;
    }
}
