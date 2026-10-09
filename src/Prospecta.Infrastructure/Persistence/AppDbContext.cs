using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Prospecta.Application.Abstractions;
using Prospecta.Application.Identity;
using Prospecta.Domain.Auditing;
using Prospecta.Domain.Businesses;
using Prospecta.Domain.Geography;
using Prospecta.Domain.Imports;
using Prospecta.Domain.Prospecting;

namespace Prospecta.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IAppDbContext
{
    public DbSet<GeographicArea> GeographicAreas => Set<GeographicArea>();
    public DbSet<BusinessCategory> BusinessCategories => Set<BusinessCategory>();
    public DbSet<StatusValue> StatusValues => Set<StatusValue>();
    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<BusinessSource> BusinessSources => Set<BusinessSource>();
    public DbSet<FieldProvenance> FieldProvenances => Set<FieldProvenance>();
    public DbSet<BusinessDataHistory> BusinessHistory => Set<BusinessDataHistory>();
    public DbSet<BusinessAssignment> BusinessAssignments => Set<BusinessAssignment>();
    public DbSet<DuplicateCandidate> DuplicateCandidates => Set<DuplicateCandidate>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ImportRow> ImportRows => Set<ImportRow>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SavedFilter> SavedFilters => Set<SavedFilter>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignParticipant> CampaignParticipants => Set<CampaignParticipant>();
    public DbSet<CampaignTarget> CampaignTargets => Set<CampaignTarget>();
    public DbSet<Outing> Outings => Set<Outing>();
    public DbSet<OutingParticipant> OutingParticipants => Set<OutingParticipant>();
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<FollowUp> FollowUps => Set<FollowUp>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<SystemFlag> SystemFlags => Set<SystemFlag>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<ReportSnapshot> ReportSnapshots => Set<ReportSnapshot>();
    public DbSet<ApplicationUser> AppUsers => Set<ApplicationUser>();

    protected override void OnConfiguring(DbContextOptionsBuilder o) =>
        // Dependents of a soft-deleted business are reached through the business; the filter on the principal is intentional.
        o.ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));

    protected override void OnModelCreating(ModelBuilder m)
    {
        base.OnModelCreating(m);
        if (Database.IsMySql()) m.UseCollation("utf8mb4_unicode_ci").HasCharSet("utf8mb4"); // accent- and case-insensitive search

        // Keys are assigned by the domain (Guid.NewGuid): without this EF would treat a child added to a tracked parent as already existing.
        foreach (var et in m.Model.GetEntityTypes().Where(t => typeof(Prospecta.Domain.Common.Entity).IsAssignableFrom(t.ClrType)))
            et.FindProperty(nameof(Prospecta.Domain.Common.Entity.Id))!.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;

        m.Entity<ApplicationUser>(e =>
        {
            e.Property(u => u.FullName).HasMaxLength(120);
            e.HasIndex(u => u.ManagerId);
        });

        m.Entity<GeographicArea>(e =>
        {
            e.Property(a => a.Name).HasMaxLength(150);
            e.Property(a => a.NormalizedName).HasMaxLength(150);
            e.Property(a => a.Code).HasMaxLength(20);
            e.HasOne(a => a.Parent).WithMany().HasForeignKey(a => a.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(a => new { a.ParentId, a.NormalizedName });
            e.HasIndex(a => new { a.Level, a.Code });
        });

        m.Entity<BusinessCategory>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(150);
            e.Property(c => c.NormalizedName).HasMaxLength(150);
            e.HasOne(c => c.Parent).WithMany().HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(c => new { c.ParentId, c.NormalizedName });
        });

        m.Entity<StatusValue>(e =>
        {
            e.Property(s => s.Code).HasMaxLength(50);
            e.Property(s => s.Label).HasMaxLength(100);
            e.HasIndex(s => new { s.Kind, s.Code }).IsUnique();
        });

        m.Entity<Business>(e =>
        {
            e.HasQueryFilter(b => !b.IsDeleted);
            e.Property(b => b.Name).HasMaxLength(200);
            e.Property(b => b.LegalName).HasMaxLength(200);
            e.Property(b => b.NormalizedName).HasMaxLength(200);
            e.Property(b => b.Description).HasMaxLength(2000);
            e.Property(b => b.Address).HasMaxLength(300);
            e.Property(b => b.Phone).HasMaxLength(30);
            e.Property(b => b.NormalizedPhone).HasMaxLength(15);
            e.Property(b => b.Website).HasMaxLength(300);
            e.Property(b => b.WebsiteHost).HasMaxLength(150);
            e.Property(b => b.GoogleMapsUrl).HasMaxLength(500);
            e.Property(b => b.SourceUrl).HasMaxLength(500);
            e.Property(b => b.PlusCode).HasMaxLength(30);
            e.Property(b => b.InternalNotes).HasMaxLength(4000);
            e.HasOne(b => b.Category).WithMany().HasForeignKey(b => b.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(b => b.SubCategory).WithMany().HasForeignKey(b => b.SubCategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(b => b.Wilaya).WithMany().HasForeignKey(b => b.WilayaId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(b => b.Daira).WithMany().HasForeignKey(b => b.DairaId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(b => b.Commune).WithMany().HasForeignKey(b => b.CommuneId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(b => b.District).WithMany().HasForeignKey(b => b.DistrictId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(b => b.CensusStatus).WithMany().HasForeignKey(b => b.CensusStatusId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(b => b.ProcessingStatus).WithMany().HasForeignKey(b => b.ProcessingStatusId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(b => b.OutcomeStatus).WithMany().HasForeignKey(b => b.OutcomeStatusId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(b => b.NormalizedName);
            e.HasIndex(b => b.NormalizedPhone);
            e.HasIndex(b => b.WebsiteHost);
            e.HasIndex(b => new { b.WilayaId, b.CommuneId });
            e.HasIndex(b => new { b.CategoryId, b.SubCategoryId });
            e.HasIndex(b => b.CensusStatusId);
            e.HasIndex(b => b.ProcessingStatusId);
            e.HasIndex(b => b.OutcomeStatusId);
            e.HasIndex(b => b.CollectedAt);
            e.HasIndex(b => b.LastVerifiedAt);
            e.HasIndex(b => b.IsDeleted);
        });

        m.Entity<BusinessSource>(e =>
        {
            e.Property(s => s.Provider).HasMaxLength(200);
            e.Property(s => s.ExternalId).HasMaxLength(200);
            e.Property(s => s.Url).HasMaxLength(500);
            e.HasOne(s => s.Business).WithMany(b => b.Sources).HasForeignKey(s => s.BusinessId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.Provider, s.ExternalId });
            e.HasIndex(s => s.BusinessId);
            e.HasIndex(s => s.ImportBatchId);
        });

        m.Entity<FieldProvenance>(e =>
        {
            e.HasKey(p => new { p.BusinessId, p.Field });
            e.Property(p => p.Field).HasMaxLength(50);
            e.HasOne<Business>().WithMany(b => b.Provenances).HasForeignKey(p => p.BusinessId).OnDelete(DeleteBehavior.Cascade);
        });

        m.Entity<BusinessDataHistory>(e =>
        {
            e.Property(h => h.Field).HasMaxLength(50);
            e.Property(h => h.OldValue).HasMaxLength(2000);
            e.Property(h => h.NewValue).HasMaxLength(2000);
            e.Property(h => h.Reason).HasMaxLength(300);
            e.HasIndex(h => new { h.BusinessId, h.CreatedAt });
        });

        m.Entity<BusinessAssignment>(e =>
        {
            e.HasOne(a => a.Business).WithMany(b => b.Assignments).HasForeignKey(a => a.BusinessId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => new { a.BusinessId, a.UserId, a.IsActive });
            e.HasIndex(a => new { a.UserId, a.IsActive });
        });

        m.Entity<DuplicateCandidate>(e =>
        {
            e.Property(d => d.Reasons).HasMaxLength(500);
            e.HasOne(d => d.BusinessA).WithMany().HasForeignKey(d => d.BusinessAId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(d => d.BusinessB).WithMany().HasForeignKey(d => d.BusinessBId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(d => new { d.BusinessAId, d.BusinessBId }).IsUnique();
            e.HasIndex(d => d.BusinessBId);
            e.HasIndex(d => new { d.Status, d.Score });
        });

        m.Entity<ImportBatch>(e =>
        {
            e.Property(i => i.FileName).HasMaxLength(255);
            e.Property(i => i.FileSha256).HasMaxLength(64);
            e.Property(i => i.HeadersJson).HasMaxLength(8000);
            e.Property(i => i.MappingJson).HasMaxLength(4000);
            e.HasIndex(i => i.FileSha256);
            e.HasIndex(i => new { i.UserId, i.CreatedAt });
        });

        m.Entity<ImportRow>(e =>
        {
            e.Property(r => r.RawJson).HasColumnType(Database.IsMySql() ? "longtext" : "TEXT");
            e.Property(r => r.Errors).HasMaxLength(1000);
            e.HasOne<ImportBatch>().WithMany(b => b.Rows).HasForeignKey(r => r.BatchId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => new { r.BatchId, r.RowNumber }).IsUnique();
            e.HasIndex(r => new { r.BatchId, r.Status });
        });

        m.Entity<AuditLog>(e =>
        {
            e.Property(a => a.Action).HasMaxLength(60);
            e.Property(a => a.EntityType).HasMaxLength(60);
            e.Property(a => a.EntityId).HasMaxLength(60);
            e.Property(a => a.UserName).HasMaxLength(200);
            e.Property(a => a.Details).HasMaxLength(2000);
            e.Property(a => a.IpAddress).HasMaxLength(45);
            e.HasIndex(a => a.CreatedAt);
            e.HasIndex(a => new { a.EntityType, a.EntityId });
            e.HasIndex(a => a.UserId);
        });

        m.Entity<SavedFilter>(e =>
        {
            e.Property(f => f.Name).HasMaxLength(80);
            e.Property(f => f.FilterJson).HasMaxLength(4000);
            e.HasIndex(f => f.UserId);
        });

        m.Entity<Campaign>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(200);
            e.Property(c => c.Objective).HasMaxLength(2000);
            e.Property(c => c.Notes).HasMaxLength(4000);
            e.Property(c => c.Budget).HasPrecision(14, 2);
            e.HasOne(c => c.Category).WithMany().HasForeignKey(c => c.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(c => c.Status);
            e.HasIndex(c => new { c.StartDate, c.EndDate });
            e.HasIndex(c => c.ManagerUserId);
        });
        m.Entity<CampaignParticipant>(e =>
        {
            e.HasOne(p => p.Campaign).WithMany(c => c.Participants).HasForeignKey(p => p.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => new { p.CampaignId, p.UserId }).IsUnique();
            e.HasIndex(p => p.UserId);
        });
        m.Entity<CampaignTarget>(e =>
        {
            e.HasOne(t => t.Campaign).WithMany(c => c.Targets).HasForeignKey(t => t.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(t => t.Business).WithMany().HasForeignKey(t => t.BusinessId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(t => new { t.CampaignId, t.BusinessId }).IsUnique();
            e.HasIndex(t => new { t.AssignedUserId, t.CampaignId });
            e.HasIndex(t => t.BusinessId);
        });
        m.Entity<Outing>(e =>
        {
            e.Property(o => o.StartPoint).HasMaxLength(300);
            e.Property(o => o.Zone).HasMaxLength(300);
            e.Property(o => o.Observations).HasMaxLength(4000);
            e.HasOne(o => o.Campaign).WithMany().HasForeignKey(o => o.CampaignId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(o => o.Date);
            e.HasIndex(o => o.CampaignId);
        });
        m.Entity<OutingParticipant>(e =>
        {
            e.HasOne(p => p.Outing).WithMany(o => o.Participants).HasForeignKey(p => p.OutingId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => new { p.OutingId, p.UserId }).IsUnique();
            e.HasIndex(p => p.UserId);
        });
        m.Entity<Visit>(e =>
        {
            e.Property(v => v.ContactMet).HasMaxLength(200);
            e.Property(v => v.Objections).HasMaxLength(2000);
            e.Property(v => v.NeedIdentified).HasMaxLength(2000);
            e.Property(v => v.RequestedInfo).HasMaxLength(2000);
            e.Property(v => v.NextAction).HasMaxLength(500);
            e.Property(v => v.Comment).HasMaxLength(4000);
            e.Property(v => v.CancelReason).HasMaxLength(300);
            e.HasOne(v => v.Business).WithMany().HasForeignKey(v => v.BusinessId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(v => new { v.BusinessId, v.ScheduledAt });
            e.HasIndex(v => new { v.UserId, v.Status, v.ScheduledAt });
            e.HasIndex(v => v.CampaignId);
            e.HasIndex(v => v.OutingId);
        });
        m.Entity<FollowUp>(e =>
        {
            e.Property(f => f.Reason).HasMaxLength(500);
            e.Property(f => f.Result).HasMaxLength(2000);
            e.HasOne(f => f.Business).WithMany().HasForeignKey(f => f.BusinessId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(f => new { f.AssignedUserId, f.Status, f.DueDate });
            e.HasIndex(f => new { f.Status, f.DueDate });
            e.HasIndex(f => f.BusinessId);
            e.HasIndex(f => f.CampaignId);
        });
        m.Entity<Expense>(e =>
        {
            e.Property(x => x.Amount).HasPrecision(14, 2);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.ReceiptReference).HasMaxLength(200);
            e.HasIndex(x => x.OutingId);
            e.HasIndex(x => x.CampaignId);
        });
        m.Entity<Notification>(e =>
        {
            e.Property(n => n.Kind).HasMaxLength(40);
            e.Property(n => n.Title).HasMaxLength(200);
            e.Property(n => n.Body).HasMaxLength(1000);
            e.Property(n => n.Link).HasMaxLength(300);
            e.Property(n => n.DedupKey).HasMaxLength(120);
            e.HasIndex(n => new { n.UserId, n.DedupKey }).IsUnique();
            e.HasIndex(n => new { n.UserId, n.ReadAt });
        });
        m.Entity<SystemFlag>(e =>
        {
            e.HasKey(f => f.Key);
            e.Property(f => f.Key).HasMaxLength(100);
            e.Property(f => f.Value).HasMaxLength(200);
        });

        m.Entity<Report>(e =>
        {
            e.Property(r => r.Title).HasMaxLength(250);
            e.Property(r => r.ParametersJson).HasColumnType(Database.IsMySql() ? "longtext" : "TEXT");
            e.Property(r => r.ValidationNote).HasMaxLength(500);
            e.HasIndex(r => new { r.OwnerUserId, r.CreatedAt });
            e.HasIndex(r => new { r.Shared, r.CreatedAt });
        });
        m.Entity<ReportSnapshot>(e =>
        {
            e.Property(r => r.ContentJson).HasColumnType(Database.IsMySql() ? "longtext" : "TEXT");
            e.HasOne(r => r.Report).WithMany(r => r.Snapshots).HasForeignKey(r => r.ReportId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => r.ReportId);
        });
    }
}
