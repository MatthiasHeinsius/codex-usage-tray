using System.Text.Json;
using static CodexUsageTray.CodexAppServerProtocol;

namespace CodexUsageTray;

internal interface ICodexAuthenticationInteraction
{
    ValueTask<bool> ConfirmAndOpenSignInAsync(
        Uri signInPage,
        CancellationToken cancellationToken);
}

internal sealed class CodexAuthenticationRecovery
{
    internal const string FailureMessage =
        "Codex sign-in expired. Run codex logout, then codex login. Refresh again.";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
    private readonly ICodexProcessExecution processExecution;
    private readonly ICodexAuthenticationInteraction? interaction;
    private int interactiveAuthenticationAttempted;

    internal CodexAuthenticationRecovery(
        ICodexProcessExecution processExecution,
        ICodexAuthenticationInteraction? interaction)
    {
        ArgumentNullException.ThrowIfNull(processExecution);
        this.processExecution = processExecution;
        this.interaction = interaction;
    }

    internal async Task<TResult> RunAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return await RunOperationAndMarkAuthenticationHealthyAsync(
                    operation,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CodexAuthenticationExpiredException)
        {
            // Codex normally refreshes managed ChatGPT credentials itself. Force one refresh
            // before asking the user to sign in again.
        }

        if (await TryRefreshAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                return await RunOperationAndMarkAuthenticationHealthyAsync(
                        operation,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CodexAuthenticationExpiredException)
            {
                // The stored refresh credential can no longer recover the session.
            }
        }

        if (interaction is not null
            && Interlocked.Exchange(ref interactiveAuthenticationAttempted, 1) == 0
            && await TrySignInAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                return await RunOperationAndMarkAuthenticationHealthyAsync(
                        operation,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CodexAuthenticationExpiredException)
            {
                // The completed sign-in did not produce usable account credentials.
            }
        }

        throw new InvalidOperationException(FailureMessage);
    }

    private async Task<TResult> RunOperationAndMarkAuthenticationHealthyAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        var result = await operation(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref interactiveAuthenticationAttempted, 0);
        return result;
    }

    private async Task<bool> TryRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await processExecution.ExchangeLinesAsync(
                "app-server --stdio",
                RequestTimeout,
                async lines =>
                {
                    await InitializeAsync(lines).ConfigureAwait(false);
                    await SendAsync(lines, new
                    {
                        id = 2,
                        method = "account/read",
                        @params = new { refreshToken = true }
                    }).ConfigureAwait(false);
                    var response = await ReadResponseAsync(lines, 2).ConfigureAwait(false);
                    return HasManagedChatGptAccount(response);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TrySignInAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await processExecution.ExchangeLinesAsync(
                "app-server --stdio",
                SignInTimeout,
                async lines =>
                {
                    await InitializeAsync(lines).ConfigureAwait(false);
                    await SendAsync(lines, new
                    {
                        id = 2,
                        method = "account/login/start",
                        @params = new
                        {
                            type = "chatgpt",
                            useHostedLoginSuccessPage = true,
                            appBrand = "codex"
                        }
                    }).ConfigureAwait(false);
                    var response = await ReadResponseAsync(lines, 2).ConfigureAwait(false);
                    if (!TryGetChatGptSignIn(response, out var loginId, out var signInPage))
                    {
                        return false;
                    }

                    if (!await interaction!.ConfirmAndOpenSignInAsync(signInPage, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        await SendAsync(lines, new
                        {
                            id = 3,
                            method = "account/login/cancel",
                            @params = new { loginId }
                        }).ConfigureAwait(false);
                        return false;
                    }

                    while (true)
                    {
                        var notification = await ReadNextMessageAsync(lines).ConfigureAwait(false);
                        if (IsCompletedSignIn(notification, loginId, out var succeeded))
                        {
                            return succeeded;
                        }
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasManagedChatGptAccount(JsonElement response) =>
        response.TryGetProperty("result", out var result)
        && result.TryGetProperty("account", out var account)
        && account.ValueKind == JsonValueKind.Object
        && account.TryGetProperty("type", out var type)
        && type.GetString() == "chatgpt";

    private static bool TryGetChatGptSignIn(
        JsonElement response,
        out string loginId,
        out Uri signInPage)
    {
        loginId = string.Empty;
        signInPage = null!;
        if (!response.TryGetProperty("result", out var result)
            || !result.TryGetProperty("loginId", out var loginIdElement)
            || string.IsNullOrWhiteSpace(loginIdElement.GetString())
            || !result.TryGetProperty("authUrl", out var authUrlElement)
            || !Uri.TryCreate(authUrlElement.GetString(), UriKind.Absolute, out var parsedPage)
            || parsedPage.Scheme != Uri.UriSchemeHttps
            || !parsedPage.IsDefaultPort
            || !string.IsNullOrEmpty(parsedPage.UserInfo)
            || parsedPage.Host is not ("chatgpt.com" or "auth.openai.com"))
        {
            return false;
        }

        loginId = loginIdElement.GetString()!;
        signInPage = parsedPage;
        return true;
    }

    private static bool IsCompletedSignIn(
        JsonElement message,
        string loginId,
        out bool succeeded)
    {
        succeeded = false;
        if (!message.TryGetProperty("method", out var method)
            || method.GetString() != "account/login/completed"
            || !message.TryGetProperty("params", out var parameters)
            || !parameters.TryGetProperty("loginId", out var completedLoginId)
            || completedLoginId.GetString() != loginId
            || !parameters.TryGetProperty("success", out var success)
            || success.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return false;
        }

        succeeded = success.GetBoolean();
        return true;
    }
}
