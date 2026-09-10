using GoldSrcOps.Web.Security;
using GoldSrcOps.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;

namespace GoldSrcOps.Web.Endpoints;

internal static class OperatorServerMonitoringEndpoints
{
    public static IEndpointRouteBuilder MapOperatorServerMonitoringEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(
                "/operator/servers/{serverId:guid}/monitoring/enable",
                EnableAsync)
            .RequireAuthorization(WebSecurity.OperatorPolicy);
        endpoints.MapPost(
                "/operator/servers/{serverId:guid}/monitoring/disable",
                DisableAsync)
            .RequireAuthorization(WebSecurity.OperatorPolicy);

        return endpoints;
    }

    private static Task<IResult> EnableAsync(
        Guid serverId,
        HttpContext context,
        IAntiforgery antiforgery,
        OperatorServerMonitoringConfirmationStore confirmations,
        IOperatorApiClient operatorApiClient,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            serverId,
            OperatorServerMonitoringAction.Enable,
            context,
            antiforgery,
            confirmations,
            operatorApiClient,
            cancellationToken);

    private static Task<IResult> DisableAsync(
        Guid serverId,
        HttpContext context,
        IAntiforgery antiforgery,
        OperatorServerMonitoringConfirmationStore confirmations,
        IOperatorApiClient operatorApiClient,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            serverId,
            OperatorServerMonitoringAction.Disable,
            context,
            antiforgery,
            confirmations,
            operatorApiClient,
            cancellationToken);

    private static async Task<IResult> UpdateAsync(
        Guid serverId,
        OperatorServerMonitoringAction action,
        HttpContext context,
        IAntiforgery antiforgery,
        OperatorServerMonitoringConfirmationStore confirmations,
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

        MonitoringFormRequest request;
        try
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            request = new MonitoringFormRequest(
                form["ConfirmationToken"].ToString(),
                string.Equals(form["Confirmed"], "true", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest();
        }

        if (!request.Confirmed)
        {
            return RedirectToForm(serverId, "invalid");
        }

        var subject = WebSecurity.GetSubject(context.User);
        if (subject is null ||
            !confirmations.TryConsume(request.ConfirmationToken, subject, serverId, action))
        {
            return RedirectToForm(serverId, "confirmation-expired");
        }

        try
        {
            var enabled = action == OperatorServerMonitoringAction.Enable;
            var result = await operatorApiClient.SetMonitoringEnabledAsync(
                serverId,
                enabled,
                cancellationToken);

            return result switch
            {
                OperatorMonitoringUpdateResult.Updated => RedirectToStatus(
                    serverId,
                    enabled ? "monitoring-enabled" : "monitoring-disabled"),
                OperatorMonitoringUpdateResult.ServerNotFound => RedirectToForm(
                    serverId,
                    "server-not-found"),
                OperatorMonitoringUpdateResult.Conflict => RedirectToForm(
                    serverId,
                    "monitoring-conflict"),
                _ => throw new InvalidOperationException(
                    $"Unsupported monitoring update result '{result}'.")
            };
        }
        catch (HttpRequestException)
        {
            return RedirectToStatus(serverId, "monitoring-unknown");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RedirectToStatus(serverId, "monitoring-unknown");
        }
    }

    private static IResult RedirectToForm(Guid serverId, string result) =>
        Results.LocalRedirect(AddResult(
            $"/operator/servers/{serverId:D}/monitoring",
            result));

    private static IResult RedirectToStatus(Guid serverId, string result) =>
        Results.LocalRedirect(AddResult(
            $"/operator/servers/{serverId:D}",
            result));

    private static string AddResult(string path, string result) =>
        QueryHelpers.AddQueryString(path, "result", result);

    private sealed record MonitoringFormRequest(
        string ConfirmationToken,
        bool Confirmed);
}
