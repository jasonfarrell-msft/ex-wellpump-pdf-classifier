using Azure;

namespace WellpumpClassifier;

public static class Readiness
{
    public static async Task<bool> CheckAsync(string dependency, Func<CancellationToken, Task> check,
        ILogger logger, CancellationToken ct)
    {
        try
        {
            await check(ct);
            return true;
        }
        catch (Exception ex)
        {
            int? status = ex switch
            {
                RequestFailedException storage => storage.Status,
                ClassifierException classifier => classifier.Status,
                HttpRequestException http when http.StatusCode is not null => (int)http.StatusCode.Value,
                _ => null
            };
            var code = ex switch
            {
                RequestFailedException failed => failed.ErrorCode,
                ClassifierException classifier => classifier.ErrorCode,
                _ => null
            };
            // Error codes are identifiers, not arbitrary service text. Discard unexpected
            // formats so URLs, headers or response content cannot become log fields.
            if (code is not null && (code.Length is < 1 or > 128
                || !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
                code = null;
            var deniedAction = ex is ClassifierException diagnostic ? diagnostic.DeniedAction : null;
            // Deliberately omit the exception object/message, bodies, headers and URLs.
            logger.LogWarning(new EventId(4101, "ReadinessDependencyFailed"),
                "Readiness dependency {Dependency} failed ({ErrorType}); status {Status}; code {ErrorCode}; denied action {DeniedAction}.",
                dependency, ex.GetType().Name, status, code, deniedAction);
            return false;
        }
    }
}
