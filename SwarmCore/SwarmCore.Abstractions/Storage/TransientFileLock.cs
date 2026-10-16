namespace SwarmCore.Abstractions.Storage;

/// <summary>
/// Retries a file-system operation across the brief locks that antivirus, the
/// search indexer, or a just-finished writer hold on a freshly written file.
/// Retries are bounded, so a file someone really keeps open still surfaces as
/// the original error.
/// </summary>
public static class TransientFileLock
{
    private const int Retries = 6; // about 0.6 s in total

    /// <summary>Windows sharing and lock violations; EAGAIN (Linux 11, Darwin 35) for .NET's advisory file locks.</summary>
    public static bool IsSharingViolation(IOException error) => (error.HResult & 0xffff) is 11 or 32 or 33 or 35;

    /// <summary>
    /// Run <paramref name="action"/>, retrying only sharing violations (and, when
    /// <paramref name="accessDeniedIsTransient"/>, access denied, which is how
    /// Windows refuses to rename a folder while a file inside it is open).
    /// <paramref name="canRetry"/> is checked before every retry, so a caller can
    /// stop once the operation no longer makes sense (its source is gone).
    /// </summary>
    public static void Retry(Action action, bool accessDeniedIsTransient = false, Func<bool>? canRetry = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception error) when (attempt < Retries
                                          && (error is IOException io && IsSharingViolation(io)
                                              || accessDeniedIsTransient && IsAccessDenied(error))
                                          && (canRetry?.Invoke() ?? true))
            {
            }
            Thread.Sleep(RetryDelay(attempt));
        }
    }

    // Directory.Move reports ERROR_ACCESS_DENIED as an IOException, other APIs as UnauthorizedAccessException.
    private static bool IsAccessDenied(Exception error)
        => error is UnauthorizedAccessException || error is IOException && (error.HResult & 0xffff) == 5;

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(10 << attempt);
}
