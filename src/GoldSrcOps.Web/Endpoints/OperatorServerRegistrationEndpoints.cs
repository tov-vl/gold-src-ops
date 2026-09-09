using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.Web.Endpoints;

internal static class OperatorServerRegistrationEndpoints
{
    public static IEndpointRouteBuilder MapOperatorServerRegistrationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(
                "/operator/servers/registrations",
                RegisterAsync)
            .RequireAuthorization(WebSecurity.OperatorPolicy);

        return endpoints;
    }

    private static async Task<IResult> RegisterAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        OperatorServerRegistrationConfirmationStore confirmations,
        IOperatorApiClient operatorApiClient,
        CancellationToken cancellationToken)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest();
        }

        if (!context.Request.HasFormContentType)
        {
            return Results.BadRequest();
        }

        RegistrationFormRequest request;
        try
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            request = new RegistrationFormRequest(
                form["ConfirmationToken"].ToString(),
                string.Equals(form["Confirmed"], "true", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest();
        }

        if (!request.Confirmed)
        {
            return RedirectToForm("invalid");
        }

        var subject = WebSecurity.GetSubject(context.User);
        if (subject is null ||
            !confirmations.TryConsume(request.ConfirmationToken, subject, out var draft))
        {
            return RedirectToForm("confirmation-expired");
        }

        try
        {
            var result = await operatorApiClient.RegisterServerAsync(
                draft,
                cancellationToken);

            if (result.Kind is OperatorServerRegistrationResultKind.Created or
                OperatorServerRegistrationResultKind.Idempotent)
            {
                var server = result.Server ?? throw new InvalidOperationException(
                    "A successful server registration must return the server.");
                return Results.LocalRedirect(AddResult(
                    $"/operator/servers/{server.Id:D}",
                    "server-registered"));
            }

            return result.Kind switch
            {
                OperatorServerRegistrationResultKind.Conflict => RedirectToForm(
                    "idempotency-conflict"),
                OperatorServerRegistrationResultKind.Rejected => RedirectToForm(
                    "rejected"),
                _ => throw new InvalidOperationException(
                    $"Unsupported server registration result '{result.Kind}'.")
            };
        }
        catch (HttpRequestException)
        {
            return RedirectToInventory("registration-unknown");
        }
        catch (InvalidDataException)
        {
            return RedirectToInventory("registration-unknown");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RedirectToInventory("registration-unknown");
        }
    }

    private static IResult RedirectToForm(string result) =>
        Results.LocalRedirect(AddResult("/operator/servers/new", result));

    private static IResult RedirectToInventory(string result) =>
        Results.LocalRedirect(AddResult("/operator/servers", result));

    private static string AddResult(string path, string result) =>
        QueryHelpers.AddQueryString(path, "result", result);

    private sealed record RegistrationFormRequest(
        string ConfirmationToken,
        bool Confirmed);
}
