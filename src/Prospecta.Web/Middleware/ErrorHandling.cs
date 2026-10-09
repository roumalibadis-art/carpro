using System.Text.Json;
using Prospecta.Application.Common;

namespace Prospecta.Web.Middleware;

/// <summary>Maps application exceptions to HTTP codes with the standard body { success, message, errors }; never leaks internals.</summary>
public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (AppException ex) when (!ctx.Response.HasStarted)
        {
            var (status, errors) = ex switch
            {
                NotFoundException => (404, []),
                ForbiddenException => (ctx.User.Identity?.IsAuthenticated == true ? 403 : 401, []),
                ConflictException => (409, []),
                ValidationException v => (400, v.Errors),
                _ => (400, (IReadOnlyList<string>)[]),
            };
            await Write(ctx, status, ex.Message, errors);
        }
        catch (Prospecta.Application.Collection.ConnectorException ex) when (!ctx.Response.HasStarted)
        {
            // A free external source is unavailable or restricted: a clear message, not a server fault.
            await Write(ctx, 502, ex.Message, []);
        }
        catch (BadHttpRequestException ex) when (!ctx.Response.HasStarted)
        {
            await Write(ctx, 400, "Requête invalide.", [ex.Message]);
        }
        catch (Exception ex) when (!ctx.Response.HasStarted && ctx.Request.Path.StartsWithSegments("/api"))
        {
            log.LogError(ex, "Unhandled error on {Path}", ctx.Request.Path);
            await Write(ctx, 500, "Une erreur interne est survenue.", []);
        }
    }

    private static async Task Write(HttpContext ctx, int status, string message, IReadOnlyList<string> errors)
    {
        ctx.Response.Clear();
        ctx.Response.StatusCode = status;
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { success = false, message, errors }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return;
        }

        ctx.Response.ContentType = "text/html; charset=utf-8";
        var title = status switch { 404 => "Introuvable", 401 or 403 => "Accès refusé", _ => "Erreur" };
        await ctx.Response.WriteAsync($"<!doctype html><meta charset=utf-8><link rel=stylesheet href=/css/site.css><body class=bare><main class=card><h1>{title}</h1><p>{System.Net.WebUtility.HtmlEncode(message)}</p><p><a href=\"/\">Retour à l'accueil</a></p></main>");
    }
}

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "same-origin";
        if (!ctx.Request.Path.StartsWithSegments("/swagger"))
        {
            h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data: https://*.tile.openstreetmap.org; style-src 'self' 'unsafe-inline'; script-src 'self'; frame-ancestors 'none'; form-action 'self'";
        }

        return next(ctx);
    }
}
