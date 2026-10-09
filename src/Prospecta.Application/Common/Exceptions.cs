namespace Prospecta.Application.Common;

public abstract class AppException(string message) : Exception(message);

public sealed class NotFoundException(string message = "Ressource introuvable.") : AppException(message);

public sealed class ForbiddenException(string message = "Accès refusé.") : AppException(message);

public sealed class ConflictException(string message) : AppException(message);

public sealed class ValidationException(IReadOnlyList<string> errors) : AppException(errors.Count > 0 ? errors[0] : "Données invalides.")
{
    public ValidationException(string error) : this([error]) { }
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

public static class Paging
{
    public const int MaxPageSize = 100;
    public static (int Page, int Size) Clamp(int page, int size) => (Math.Max(1, page), Math.Clamp(size <= 0 ? 25 : size, 1, MaxPageSize));
}
