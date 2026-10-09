using Prospecta.Domain.Businesses;
using Prospecta.Domain.Common;

namespace Prospecta.Application.Businesses;

public enum FieldChange { Unchanged, Changed, Blocked }

/// <summary>
/// Single place that mutates tracked business fields. It records provenance and history and enforces the rule
/// "a confirmed value is never silently overwritten by an external or estimated one".
/// </summary>
public sealed class FieldUpdater(Business business, TimeProvider clock, Guid? userId, bool canOverrideConfirmed, Guid? sourceId = null)
{
    private sealed record Accessor(string Name, Func<Business, string?> Get, Action<Business, string?> Set);

    public static readonly IReadOnlyList<string> TrackedFields =
    [
        "Name", "LegalName", "Description", "Address", "Phone", "Website", "GoogleMapsUrl", "SourceUrl", "PlusCode",
        "Latitude", "Longitude", "CategoryId", "SubCategoryId", "WilayaId", "DairaId", "CommuneId", "DistrictId",
    ];

    private static readonly Dictionary<string, Accessor> Map = new[]
    {
        new Accessor("Name", b => b.Name, (b, v) => b.Name = v ?? string.Empty),
        new Accessor("LegalName", b => b.LegalName, (b, v) => b.LegalName = v),
        new Accessor("Description", b => b.Description, (b, v) => b.Description = v),
        new Accessor("Address", b => b.Address, (b, v) => b.Address = v),
        new Accessor("Phone", b => b.Phone, (b, v) => b.Phone = v),
        new Accessor("Website", b => b.Website, (b, v) => b.Website = v),
        new Accessor("GoogleMapsUrl", b => b.GoogleMapsUrl, (b, v) => b.GoogleMapsUrl = v),
        new Accessor("SourceUrl", b => b.SourceUrl, (b, v) => b.SourceUrl = v),
        new Accessor("PlusCode", b => b.PlusCode, (b, v) => b.PlusCode = v),
        new Accessor("Latitude", b => BusinessRules.FormatDouble(b.Latitude) is { Length: > 0 } s ? s : null,
            (b, v) => b.Latitude = BusinessRules.TryParseDouble(v, out var d) ? d : null),
        new Accessor("Longitude", b => BusinessRules.FormatDouble(b.Longitude) is { Length: > 0 } s ? s : null,
            (b, v) => b.Longitude = BusinessRules.TryParseDouble(v, out var d) ? d : null),
        new Accessor("CategoryId", b => b.CategoryId?.ToString(), (b, v) => b.CategoryId = ParseGuid(v)),
        new Accessor("SubCategoryId", b => b.SubCategoryId?.ToString(), (b, v) => b.SubCategoryId = ParseGuid(v)),
        new Accessor("WilayaId", b => b.WilayaId?.ToString(), (b, v) => b.WilayaId = ParseGuid(v)),
        new Accessor("DairaId", b => b.DairaId?.ToString(), (b, v) => b.DairaId = ParseGuid(v)),
        new Accessor("CommuneId", b => b.CommuneId?.ToString(), (b, v) => b.CommuneId = ParseGuid(v)),
        new Accessor("DistrictId", b => b.DistrictId?.ToString(), (b, v) => b.DistrictId = ParseGuid(v)),
    }.ToDictionary(a => a.Name);

    private static Guid? ParseGuid(string? v) => Guid.TryParse(v, out var g) ? g : null;

    public List<BusinessDataHistory> History { get; } = [];
    public List<string> BlockedFields { get; } = [];

    public static string? GetValue(Business b, string field) => Map[field].Get(b);

    public FieldChange Apply(string field, string? newValue, FieldOrigin origin, string? reason = null)
    {
        var acc = Map[field];
        var now = clock.GetUtcNow().UtcDateTime;
        newValue = string.IsNullOrWhiteSpace(newValue) ? null : newValue.Trim();
        var current = acc.Get(business);
        var prov = business.Provenances.FirstOrDefault(p => p.Field == field);

        // An external/estimated source reporting "nothing" is not a statement: never blank existing data.
        if (newValue is null && origin is FieldOrigin.External or FieldOrigin.Estimated)
        {
            return FieldChange.Unchanged;
        }

        if (string.Equals(current, newValue, StringComparison.Ordinal))
        {
            // Same value re-asserted with a stronger origin (e.g. human confirmation) upgrades provenance only.
            if (newValue is not null && origin == FieldOrigin.Confirmed && prov?.Origin != FieldOrigin.Confirmed)
            {
                SetProvenance(field, origin, prov, now);
            }

            return FieldChange.Unchanged;
        }

        if (prov?.Origin == FieldOrigin.Confirmed && current is not null)
        {
            var allowed = origin == FieldOrigin.Confirmed || (origin == FieldOrigin.Manual && canOverrideConfirmed);
            if (!allowed)
            {
                BlockedFields.Add(field);
                return FieldChange.Blocked;
            }
        }

        acc.Set(business, newValue);
        History.Add(new BusinessDataHistory
        {
            BusinessId = business.Id,
            Field = field,
            OldValue = current,
            NewValue = newValue,
            Origin = origin,
            ChangedByUserId = userId,
            Reason = reason,
            CreatedAt = now,
        });
        if (newValue is null)
        {
            if (prov is not null)
            {
                business.Provenances.Remove(prov);
            }
        }
        else
        {
            SetProvenance(field, origin, prov, now);
        }

        return FieldChange.Changed;
    }

    /// <summary>Marks every currently known tracked value as confirmed (called when a person verifies the record).</summary>
    public void ConfirmAll()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var field in TrackedFields)
        {
            if (Map[field].Get(business) is not null)
            {
                SetProvenance(field, FieldOrigin.Confirmed, business.Provenances.FirstOrDefault(p => p.Field == field), now);
            }
        }
    }

    private void SetProvenance(string field, FieldOrigin origin, FieldProvenance? existing, DateTime now)
    {
        if (existing is null)
        {
            business.Provenances.Add(new FieldProvenance
            {
                BusinessId = business.Id, Field = field, Origin = origin, SourceId = sourceId,
                UpdatedAt = now, UpdatedByUserId = userId,
            });
            return;
        }

        existing.Origin = origin;
        existing.SourceId = sourceId ?? existing.SourceId;
        existing.UpdatedAt = now;
        existing.UpdatedByUserId = userId;
    }
}
